using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Networking.Transport.Relay;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using UnityEngine;
using UnityEngine.SceneManagement;

public enum LobbyState
{
    Initializing, // forbinder til Unity Services
    Menu,         // vælg/opret lobby
    InLobby,      // i en lobby (hold, klar, indstillinger)
    Starting,     // spillet er ved at starte
    InGame
}

/// <summary>
/// Styrer hele vejen fra menu til kampstart:
///  - Unity Authentication (anonymt login), Lobby (offentlig/privat med kode) og Relay (forbindelse over internettet)
///  - Hold, klar-status og spilindstillinger
///  - Starter Netcode (host/client), indlæser spilscenen og spawner spillerne (hold sammen)
///
/// Skal ligge på et ROD-GameObject i lobby-scenen (det bliver DontDestroyOnLoad).
/// Brug LobbyUI på samme objekt til at vise det hele.
/// </summary>
public class LobbyManager : MonoBehaviour
{
    public static LobbyManager Instance { get; private set; }

    [Header("Scener (skal ligge i Build Profiles → Scene List)")]
    [SerializeField] private string lobbySceneName = "LobbyScene";
    [SerializeField] private string gameSceneName = "GameScene";

    [Header("Spil")]
    [Tooltip("Player-prefab (med NetworkObject). Spawnes af serveren, når spilscenen er indlæst. " +
             "Lad NetworkManagerens eget Player Prefab-felt stå tomt.")]
    [SerializeField] private NetworkObject playerPrefab;
    [Tooltip("Hvor mange spillere der mindst skal være i lobbyen, før værten kan starte. Sæt til 1 for at teste alene.")]
    [SerializeField] private int minPlayersToStart = 2;
    [Tooltip("Hvor længe værten venter på, at alle forbinder, før spillet starter uden dem.")]
    [SerializeField] private float clientConnectTimeout = 30f;

    [Header("Test")]
    [Tooltip("I editoren bruges en ny tilfældig profil hver gang, så flere editor-instanser kan logge ind som forskellige spillere.")]
    [SerializeField] private bool randomProfileInEditor = true;

    // Nøgler i lobbyens data
    private const string KeyTeamSize = "TeamSize";
    private const string KeyHandSpeed = "HandSpeed";
    private const string KeyLoot = "Loot";
    private const string KeySettingsVersion = "SetVer";
    private const string KeyRelayCode = "RelayCode";

    // Nøgler i spillerdata
    private const string PKeyName = "Name";
    private const string PKeyTeam = "Team";
    private const string PKeyReady = "Ready";

    // Offentlig tilstand (læses af UI)
    public LobbyState State { get; private set; } = LobbyState.Initializing;
    public bool IsBusy { get; private set; }
    public string ErrorMessage { get; private set; } = "";
    public string StatusMessage { get; private set; } = "";
    public string PlayerName { get; set; }
    public Lobby CurrentLobby { get; private set; }
    public IReadOnlyList<Lobby> PublicLobbies { get; private set; } = new List<Lobby>();
    public MatchSettings Settings { get; private set; } = new MatchSettings();
    public int MyTeam { get; private set; }
    public bool MyReady { get; private set; }
    public int MinPlayersToStart => minPlayersToStart;

    public string MyPlayerId => AuthenticationService.Instance.PlayerId;
    public bool IsHost => CurrentLobby != null && CurrentLobby.HostId == MyPlayerId;

    // Intern tilstand
    private bool isDuplicate;
    private bool networkEventsHooked;
    private bool acceptingConnections;
    private bool pollInFlight;
    private bool listRefreshing;
    private bool playerUpdateBusy;
    private bool needsStateReset;
    private bool settingsDirty;
    private int lastSeenVersion;
    private int pollFailures;
    private float nextPoll;
    private float nextHeartbeat;
    private float nextListRefresh;
    private float settingsPushTime;

    // ======================================================================
    // Livscyklus
    // ======================================================================

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            // Vi er tilbage i lobby-scenen: den originale (persistente) LobbyManager lever allerede
            isDuplicate = true;
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private async void Start()
    {
        if (isDuplicate) return;

        PlayerName = "Spiller" + UnityEngine.Random.Range(1000, 9999);

        if (NetworkManager.Singleton == null)
        {
            ErrorMessage = "Der er ingen NetworkManager i scenen.";
            return;
        }

        SubscribeNetworkEvents();
        await InitializeAsync();
    }

    private void OnDestroy()
    {
        if (isDuplicate || Instance != this) return;

        if (networkEventsHooked && NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
        }
        Instance = null;
    }

    private void OnApplicationQuit()
    {
        // Forsøg at rydde op, så lobbyen ikke hænger i live
        if (State == LobbyState.InLobby && CurrentLobby != null)
            _ = SafeLeaveLobby(CurrentLobby.Id, IsHost);
    }

    private void Update()
    {
        if (isDuplicate) return;

        // Host: hold lobbyen i live under lobby-fasen og under kampstart
        bool hostAlive = (State == LobbyState.InLobby || State == LobbyState.Starting) && CurrentLobby != null && IsHost;
        if (hostAlive && Time.time >= nextHeartbeat)
        {
            nextHeartbeat = Time.time + 15f;
            _ = SendHeartbeat(CurrentLobby.Id);
        }

        if (State == LobbyState.InLobby && CurrentLobby != null)
        {
            if (Time.time >= nextPoll && !pollInFlight)
            {
                nextPoll = Time.time + 2f;
                _ = PollLobbyAsync();
            }

            if (settingsDirty && Time.time >= settingsPushTime && !IsBusy)
                _ = Run(PushSettingsAsync);

            if (needsStateReset && !playerUpdateBusy)
            {
                needsStateReset = false;
                _ = UpdateMyPlayerAsync(0, false);
            }
        }

        // Opdater listen over offentlige lobbyer automatisk i menuen
        if (State == LobbyState.Menu && !IsBusy && !listRefreshing && Time.time >= nextListRefresh)
        {
            nextListRefresh = Time.time + 5f;
            _ = RefreshListSilently();
        }
    }

    // ======================================================================
    // Initialisering
    // ======================================================================

    public void Retry()
    {
        if (State == LobbyState.Initializing && !IsBusy)
            _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        IsBusy = true;
        ErrorMessage = "";
        try
        {
            var options = new InitializationOptions();
#if UNITY_EDITOR
            if (randomProfileInEditor)
                options.SetProfile("p" + Guid.NewGuid().ToString("N").Substring(0, 12));
#endif
            await UnityServices.InitializeAsync(options);

            if (!AuthenticationService.Instance.IsSignedIn)
                await AuthenticationService.Instance.SignInAnonymouslyAsync();

            State = LobbyState.Menu;
            nextListRefresh = 0f;
        }
        catch (Exception e)
        {
            ErrorMessage = "Kunne ikke forbinde til Unity Services. Er projektet linket, og er Lobby og Relay slået til i Unity Dashboard?\n" + e.Message;
            Debug.LogException(e);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ======================================================================
    // Menu: opret / join
    // ======================================================================

    public void CreateLobby(bool isPrivate, int maxPlayers) => _ = Run(() => CreateLobbyInternal(isPrivate, maxPlayers));
    public void JoinByCode(string code) => _ = Run(() => JoinByCodeInternal(code));
    public void JoinById(string lobbyId) => _ = Run(() => JoinByIdInternal(lobbyId));
    public void QuickJoin() => _ = Run(QuickJoinInternal);
    public void RefreshPublicLobbies() => _ = Run(RefreshListInternal);

    private async Task CreateLobbyInternal(bool isPrivate, int maxPlayers)
    {
        MyTeam = 0;
        MyReady = false;
        Settings = new MatchSettings { MaxPlayers = maxPlayers };

        var options = new CreateLobbyOptions
        {
            IsPrivate = isPrivate,
            Player = BuildPlayer(),
            Data = BuildLobbyData(Settings, 0)
        };

        Lobby lobby = await LobbyService.Instance.CreateLobbyAsync(PlayerName + "s lobby", maxPlayers, options);
        EnterLobby(lobby);
    }

    private async Task JoinByCodeInternal(string code)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new Exception("Skriv en lobbykode.");
        MyTeam = 0;
        MyReady = false;

        Lobby lobby = await LobbyService.Instance.JoinLobbyByCodeAsync(code.Trim().ToUpperInvariant(),
            new JoinLobbyByCodeOptions { Player = BuildPlayer() });
        EnterLobby(lobby);
    }

    private async Task JoinByIdInternal(string lobbyId)
    {
        MyTeam = 0;
        MyReady = false;

        Lobby lobby = await LobbyService.Instance.JoinLobbyByIdAsync(lobbyId,
            new JoinLobbyByIdOptions { Player = BuildPlayer() });
        EnterLobby(lobby);
    }

    private async Task QuickJoinInternal()
    {
        MyTeam = 0;
        MyReady = false;

        Lobby lobby = await LobbyService.Instance.QuickJoinLobbyAsync(new QuickJoinLobbyOptions { Player = BuildPlayer() });
        EnterLobby(lobby);
    }

    private async Task RefreshListInternal()
    {
        QueryResponse response = await LobbyService.Instance.QueryLobbiesAsync(BuildQuery());
        PublicLobbies = response.Results;
    }

    private async Task RefreshListSilently()
    {
        listRefreshing = true;
        try
        {
            QueryResponse response = await LobbyService.Instance.QueryLobbiesAsync(BuildQuery());
            PublicLobbies = response.Results;
        }
        catch (Exception e)
        {
            Debug.LogWarning("Kunne ikke hente lobbyliste: " + e.Message);
        }
        finally
        {
            listRefreshing = false;
        }
    }

    private static QueryLobbiesOptions BuildQuery() => new QueryLobbiesOptions
    {
        Count = 20,
        Filters = new List<QueryFilter>
        {
            new QueryFilter(QueryFilter.FieldOptions.AvailableSlots, "0", QueryFilter.OpOptions.GT)
        },
        Order = new List<QueryOrder>
        {
            new QueryOrder(false, QueryOrder.FieldOptions.Created)
        }
    };

    private void EnterLobby(Lobby lobby)
    {
        CurrentLobby = lobby;
        ApplySettingsFromLobby(lobby);
        lastSeenVersion = ReadInt(lobby, KeySettingsVersion, 0);
        settingsDirty = false;
        needsStateReset = false;
        pollFailures = 0;
        nextPoll = Time.time + 2f;
        nextHeartbeat = Time.time + 10f;
        State = LobbyState.InLobby;
    }

    // ======================================================================
    // I lobbyen: polling, hold, klar, indstillinger
    // ======================================================================

    private async Task PollLobbyAsync()
    {
        if (CurrentLobby == null) return;
        pollInFlight = true;
        string id = CurrentLobby.Id;

        try
        {
            Lobby lobby = await LobbyService.Instance.GetLobbyAsync(id);

            // Tilstanden kan have ændret sig, mens vi ventede
            if (State != LobbyState.InLobby || CurrentLobby == null || CurrentLobby.Id != id) return;
            pollFailures = 0;
            CurrentLobby = lobby;

            // Er vi blevet smidt ud?
            if (!lobby.Players.Exists(p => p.Id == MyPlayerId))
            {
                ResetToMenu("Du blev fjernet fra lobbyen.");
                return;
            }

            // Indstillinger (men overskriv ikke værtens ændringer, der endnu ikke er sendt)
            if (!settingsDirty)
                ApplySettingsFromLobby(lobby);

            // Ændrede værten indstillingerne? Så nulstilles hold og klar-status hos alle
            int version = ReadInt(lobby, KeySettingsVersion, 0);
            if (version > lastSeenVersion)
            {
                lastSeenVersion = version;
                if (MyTeam != 0 || MyReady) needsStateReset = true;
            }

            // Har værten startet spillet? Så forbinder vi via Relay
            if (!IsHost && lobby.Data != null
                && lobby.Data.TryGetValue(KeyRelayCode, out DataObject relay)
                && !string.IsNullOrEmpty(relay.Value))
            {
                _ = JoinGameAsClient(relay.Value);
            }
        }
        catch (LobbyServiceException e) when (e.Reason == LobbyExceptionReason.LobbyNotFound)
        {
            if (State == LobbyState.InLobby && CurrentLobby != null && CurrentLobby.Id == id)
                ResetToMenu("Lobbyen blev lukket.");
        }
        catch (Exception e)
        {
            Debug.LogWarning("Lobby-polling fejlede: " + e.Message);
            if (++pollFailures >= 5 && State == LobbyState.InLobby)
                ResetToMenu("Forbindelsen til lobbyen blev mistet.");
        }
        finally
        {
            pollInFlight = false;
        }
    }

    private async Task SendHeartbeat(string lobbyId)
    {
        try { await LobbyService.Instance.SendHeartbeatPingAsync(lobbyId); }
        catch (Exception e) { Debug.LogWarning("Heartbeat fejlede: " + e.Message); }
    }

    /// <summary>Vælg hold (0 = intet hold). Nulstiller klar-status.</summary>
    public void SetTeam(int team)
    {
        if (State != LobbyState.InLobby || !Settings.HasTeams) return;
        if (team > 0 && TeamMemberCount(team, excludePlayerId: MyPlayerId) >= Settings.TeamSize) return;
        _ = UpdateMyPlayerAsync(team, false);
    }

    public void ToggleReady()
    {
        if (State != LobbyState.InLobby) return;
        if (!MyReady && Settings.HasTeams && MyTeam <= 0) { ErrorMessage = "Vælg et hold først."; return; }
        ErrorMessage = "";
        _ = UpdateMyPlayerAsync(MyTeam, !MyReady);
    }

    private async Task UpdateMyPlayerAsync(int team, bool ready)
    {
        if (playerUpdateBusy || CurrentLobby == null) return;
        playerUpdateBusy = true;
        try
        {
            Lobby lobby = await LobbyService.Instance.UpdatePlayerAsync(CurrentLobby.Id, MyPlayerId, new UpdatePlayerOptions
            {
                Data = new Dictionary<string, PlayerDataObject>
                {
                    { PKeyTeam, new PlayerDataObject(PlayerDataObject.VisibilityOptions.Member, team.ToString()) },
                    { PKeyReady, new PlayerDataObject(PlayerDataObject.VisibilityOptions.Member, ready ? "1" : "0") }
                }
            });
            MyTeam = team;
            MyReady = ready;
            if (State == LobbyState.InLobby) CurrentLobby = lobby;
        }
        catch (Exception e)
        {
            ErrorMessage = "Kunne ikke opdatere din status: " + e.Message;
        }
        finally
        {
            playerUpdateBusy = false;
        }
    }

    // ---------- Indstillinger (kun værten) ----------

    public void ChangeTeamSize(int delta)
    {
        if (!IsHost || State != LobbyState.InLobby) return;
        Settings.TeamSize = Mathf.Clamp(Settings.TeamSize + delta, 1, 4);
        MarkSettingsDirty();
    }

    public void ChangeHandSpeed(int steps)
    {
        if (!IsHost || State != LobbyState.InLobby) return;
        Settings.HandSpeedPercent = Mathf.Clamp(Settings.HandSpeedPercent + steps * 25, 50, 200);
        MarkSettingsDirty();
    }

    public void ChangeLoot(int steps)
    {
        if (!IsHost || State != LobbyState.InLobby) return;
        Settings.LootPercent = Mathf.Clamp(Settings.LootPercent + steps * 25, 50, 200);
        MarkSettingsDirty();
    }

    // Samler flere hurtige ændringer til ét kald (Lobby-tjenesten har strenge rate limits)
    private void MarkSettingsDirty()
    {
        settingsDirty = true;
        settingsPushTime = Time.time + 1f;
    }

    private async Task PushSettingsAsync()
    {
        if (CurrentLobby == null || !IsHost) { settingsDirty = false; return; }
        settingsDirty = false;

        int newVersion = lastSeenVersion + 1;
        Lobby lobby = await LobbyService.Instance.UpdateLobbyAsync(CurrentLobby.Id, new UpdateLobbyOptions
        {
            Data = BuildLobbyData(Settings, newVersion)
        });

        CurrentLobby = lobby;
        lastSeenVersion = newVersion;

        // Værten nulstiller også sit eget hold og sin klar-status
        if (MyTeam != 0 || MyReady) needsStateReset = true;
    }

    // ---------- Hjælpere til spillerliste ----------

    public int TeamMemberCount(int team, string excludePlayerId = null)
    {
        if (CurrentLobby == null) return 0;
        int count = 0;
        foreach (Player p in CurrentLobby.Players)
            if (p.Id != excludePlayerId && GetTeam(p) == team) count++;
        return count;
    }

    public static string GetName(Player p)
        => p.Data != null && p.Data.TryGetValue(PKeyName, out PlayerDataObject d) ? d.Value : "???";

    public static int GetTeam(Player p)
        => p.Data != null && p.Data.TryGetValue(PKeyTeam, out PlayerDataObject d) && int.TryParse(d.Value, out int t) ? t : 0;

    public static bool IsReady(Player p)
        => p.Data != null && p.Data.TryGetValue(PKeyReady, out PlayerDataObject d) && d.Value == "1";

    public int NumberOfTeams => Settings.HasTeams ? Mathf.CeilToInt(Settings.MaxPlayers / (float)Settings.TeamSize) : 0;

    // ======================================================================
    // Forlad / smid ud
    // ======================================================================

    public void KickPlayer(string playerId)
    {
        if (!IsHost || playerId == MyPlayerId || CurrentLobby == null) return;
        string lobbyId = CurrentLobby.Id;
        _ = Run(async () => { await LobbyService.Instance.RemovePlayerAsync(lobbyId, playerId); });
    }

    public void LeaveLobby()
    {
        if (CurrentLobby == null) return;
        string lobbyId = CurrentLobby.Id;
        bool wasHost = IsHost;
        ResetToMenu("");
        _ = SafeLeaveLobby(lobbyId, wasHost);
    }

    private async Task SafeLeaveLobby(string lobbyId, bool wasHost)
    {
        try
        {
            if (wasHost) await LobbyService.Instance.DeleteLobbyAsync(lobbyId);
            else await LobbyService.Instance.RemovePlayerAsync(lobbyId, AuthenticationService.Instance.PlayerId);
        }
        catch (Exception e)
        {
            Debug.LogWarning("Kunne ikke forlade lobbyen pænt: " + e.Message);
        }
    }

    private void ResetToMenu(string message)
    {
        CurrentLobby = null;
        MyTeam = 0;
        MyReady = false;
        settingsDirty = false;
        needsStateReset = false;
        State = LobbyState.Menu;
        ErrorMessage = message;
        StatusMessage = "";
        nextListRefresh = 0f;
    }

    /// <summary>Forlad en igangværende kamp og gå tilbage til lobby-menuen.</summary>
    public void LeaveGame()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm != null && nm.IsListening) nm.Shutdown();

        MatchRegistry.Clear();
        acceptingConnections = false;
        ResetToMenu("");

        if (SceneManager.GetActiveScene().name != lobbySceneName)
            SceneManager.LoadScene(lobbySceneName);
    }

    // ======================================================================
    // Kampstart (host)
    // ======================================================================

    public bool CanStart(out string reason)
    {
        reason = "";
        if (CurrentLobby == null) { reason = "Ingen lobby."; return false; }
        if (playerPrefab == null) { reason = "Player-prefab mangler på LobbyManager."; return false; }

        List<Player> players = CurrentLobby.Players;
        if (players.Count < minPlayersToStart)
        {
            reason = $"Der skal mindst være {minPlayersToStart} spillere.";
            return false;
        }

        foreach (Player p in players)
        {
            if (!IsReady(p)) { reason = "Alle skal trykke klar."; return false; }
        }

        if (Settings.HasTeams)
        {
            var counts = new Dictionary<int, int>();
            foreach (Player p in players)
            {
                int team = GetTeam(p);
                if (team <= 0) { reason = "Alle skal vælge et hold."; return false; }

                counts.TryGetValue(team, out int c);
                counts[team] = c + 1;
                if (counts[team] > Settings.TeamSize) { reason = "Et hold er for stort."; return false; }
            }
        }

        return true;
    }

    public void StartGame() => _ = Run(StartGameInternal);

    private async Task StartGameInternal()
    {
        if (!IsHost || State != LobbyState.InLobby) return;
        if (!CanStart(out string reason)) throw new Exception(reason);

        NetworkManager nm = NetworkManager.Singleton;
        Lobby lobby = CurrentLobby;
        int expectedPlayers = lobby.Players.Count;

        State = LobbyState.Starting;
        StatusMessage = "Opretter forbindelse...";

        try
        {
            // 1) Reservér plads på en Relay-server og hent en kode, de andre kan bruge
            int maxConnections = Mathf.Max(1, lobby.MaxPlayers - 1);
            Allocation allocation = await RelayService.Instance.CreateAllocationAsync(maxConnections);
            string joinCode = await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);

            // 2) Start host
            nm.GetComponent<UnityTransport>().SetRelayServerData(AllocationUtils.ToRelayServerData(allocation, "dtls"));
            MatchRegistry.Clear();
            MatchRegistry.Settings = Settings.Clone();
            PrepareNetworkManager();
            acceptingConnections = true;

            if (!nm.StartHost())
                throw new Exception("Kunne ikke starte host.");

            // 3) Fortæl de andre i lobbyen, hvordan de forbinder, og lås lobbyen
            StatusMessage = "Venter på spillerne...";
            await LobbyService.Instance.UpdateLobbyAsync(lobby.Id, new UpdateLobbyOptions
            {
                IsLocked = true,
                Data = new Dictionary<string, DataObject>
                {
                    { KeyRelayCode, new DataObject(DataObject.VisibilityOptions.Member, joinCode) }
                }
            });

            // 4) Vent på, at alle er forbundet (eller til tiden løber ud)
            float deadline = Time.realtimeSinceStartup + clientConnectTimeout;
            while (nm.ConnectedClientsIds.Count < expectedPlayers && Time.realtimeSinceStartup < deadline)
                await Task.Yield();

            acceptingConnections = false;

            // Lobbyen er ikke længere nødvendig
            try { await LobbyService.Instance.DeleteLobbyAsync(lobby.Id); }
            catch (Exception e) { Debug.LogWarning("Kunne ikke slette lobbyen: " + e.Message); }
            CurrentLobby = null;

            // 5) Indlæs spilscenen for alle; spillerne spawnes, når den er klar
            nm.SceneManager.OnLoadEventCompleted += OnGameSceneLoaded;
            SceneEventProgressStatus status = nm.SceneManager.LoadScene(gameSceneName, LoadSceneMode.Single);
            if (status != SceneEventProgressStatus.Started)
                throw new Exception($"Kunne ikke indlæse '{gameSceneName}' ({status}). Ligger scenen i Build Profiles?");

            State = LobbyState.InGame;
            StatusMessage = "";
        }
        catch
        {
            acceptingConnections = false;
            if (nm.IsListening) nm.Shutdown();
            State = CurrentLobby != null ? LobbyState.InLobby : LobbyState.Menu;
            StatusMessage = "";
            throw;
        }
    }

    // ======================================================================
    // Kampstart (klient)
    // ======================================================================

    private async Task JoinGameAsClient(string relayJoinCode)
    {
        if (State != LobbyState.InLobby) return;

        NetworkManager nm = NetworkManager.Singleton;
        State = LobbyState.Starting;
        StatusMessage = "Forbinder til spillet...";

        try
        {
            JoinAllocation allocation = await RelayService.Instance.JoinAllocationAsync(relayJoinCode);
            nm.GetComponent<UnityTransport>().SetRelayServerData(AllocationUtils.ToRelayServerData(allocation, "dtls"));

            MatchRegistry.Clear();
            MatchRegistry.Settings = Settings.Clone();
            PrepareNetworkManager();

            if (!nm.StartClient())
                throw new Exception("Kunne ikke starte klient.");

            // State skifter til InGame, når forbindelsen er oprettet (OnClientConnected)
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            if (nm.IsListening) nm.Shutdown();
            ResetToMenu("Kunne ikke forbinde til spillet: " + e.Message);
        }
    }

    // ======================================================================
    // Netcode: forbindelse og spawn
    // ======================================================================

    private void SubscribeNetworkEvents()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || networkEventsHooked) return;

        nm.OnClientConnectedCallback += OnClientConnected;
        nm.OnClientDisconnectCallback += OnClientDisconnected;
        networkEventsHooked = true;
    }

    private void OnClientConnected(ulong clientId)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (!nm.IsServer && clientId == nm.LocalClientId && State == LobbyState.Starting)
        {
            State = LobbyState.InGame;
            StatusMessage = "";
            CurrentLobby = null;
        }
    }

    private void OnClientDisconnected(ulong clientId)
    {
        NetworkManager nm = NetworkManager.Singleton;

        // Kun relevant for en ren klient, der mister forbindelsen til værten
        if (nm.IsServer || clientId != nm.LocalClientId) return;
        if (State != LobbyState.Starting && State != LobbyState.InGame) return;

        string reason = string.IsNullOrEmpty(nm.DisconnectReason) ? "Forbindelsen til værten blev afbrudt." : nm.DisconnectReason;

        if (nm.IsListening) nm.Shutdown();
        MatchRegistry.Clear();
        ResetToMenu(reason);

        if (SceneManager.GetActiveScene().name != lobbySceneName)
            SceneManager.LoadScene(lobbySceneName);
    }

    /// <summary>Sætter navn og hold på forbindelsen og slår godkendelse til.</summary>
    private void PrepareNetworkManager()
    {
        NetworkManager nm = NetworkManager.Singleton;
        nm.NetworkConfig.ConnectionApproval = true;
        nm.ConnectionApprovalCallback = ApprovalCheck;

        var payload = new ConnectionPayload { PlayerId = MyPlayerId, Name = PlayerName, Team = MyTeam };
        nm.NetworkConfig.ConnectionData = Encoding.UTF8.GetBytes(JsonUtility.ToJson(payload));
    }

    // Kører på serveren, hver gang nogen forbinder (også værten selv)
    private void ApprovalCheck(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
    {
        // Spillerne spawnes manuelt, når spilscenen er indlæst
        response.CreatePlayerObject = false;

        if (!acceptingConnections)
        {
            response.Approved = false;
            response.Reason = "Spillet er allerede startet.";
            return;
        }

        try
        {
            ConnectionPayload info = JsonUtility.FromJson<ConnectionPayload>(Encoding.UTF8.GetString(request.Payload));
            if (string.IsNullOrEmpty(info.PlayerId)) throw new Exception("Mangler spiller-id");

            MatchRegistry.Players[request.ClientNetworkId] = info;
            response.Approved = true;
        }
        catch (Exception e)
        {
            Debug.LogWarning("Afviste forbindelse: " + e.Message);
            response.Approved = false;
            response.Reason = "Ugyldige forbindelsesdata.";
        }
    }

    private void OnGameSceneLoaded(string sceneName, LoadSceneMode mode, List<ulong> clientsCompleted, List<ulong> clientsTimedOut)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer || sceneName != gameSceneName) return;

        nm.SceneManager.OnLoadEventCompleted -= OnGameSceneLoaded;
        SpawnPlayers(clientsCompleted);
    }

    /// <summary>
    /// Hold spawner samlet omkring ét spawn-punkt (i en lille cirkel), solo-spillere får hvert deres.
    /// Hver gruppe får et tilfældigt punkt. Er der flere grupper end punkter, genbruges punkter.
    /// </summary>
    private void SpawnPlayers(List<ulong> clients)
    {
        SpawnPoint[] points = FindObjectsByType<SpawnPoint>(FindObjectsSortMode.None);
        Array.Sort(points, (a, b) => string.CompareOrdinal(a.name, b.name));

        if (points.Length == 0)
            Debug.LogWarning("Ingen SpawnPoint i spilscenen! Spillerne spawner i (0,0,0).");

        // Grupper spillerne: hold-nummer for hold, ellers en unik gruppe pr. solo-spiller
        var groups = new Dictionary<int, List<ulong>>();
        var groupOrder = new List<int>();
        foreach (ulong clientId in clients)
        {
            int team = MatchRegistry.TryGet(clientId, out ConnectionPayload info) ? info.Team : 0;
            int key = team > 0 ? team : -1 - (int)clientId;

            if (!groups.TryGetValue(key, out List<ulong> members))
            {
                members = new List<ulong>();
                groups[key] = members;
                groupOrder.Add(key);
            }
            members.Add(clientId);
        }

        // Bland rækkefølgen, så grupperne ikke altid får de samme punkter
        for (int i = groupOrder.Count - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            (groupOrder[i], groupOrder[j]) = (groupOrder[j], groupOrder[i]);
        }

        if (points.Length > 0 && groupOrder.Count > points.Length)
            Debug.LogWarning($"Der er {groupOrder.Count} grupper, men kun {points.Length} spawn-punkter. Nogle deler punkt.");

        for (int g = 0; g < groupOrder.Count; g++)
        {
            List<ulong> members = groups[groupOrder[g]];
            Vector3 center = Vector3.zero;
            Quaternion rotation = Quaternion.identity;
            if (points.Length > 0)
            {
                SpawnPoint sp = points[g % points.Length];
                center = sp.transform.position;
                rotation = sp.transform.rotation;
            }

            for (int m = 0; m < members.Count; m++)
            {
                ulong clientId = members[m];

                Vector3 offset = Vector3.zero;
                if (members.Count > 1)
                {
                    float angle = m * (360f / members.Count);
                    offset = Quaternion.Euler(0f, angle, 0f) * Vector3.forward * 1.5f;
                }

                NetworkObject player = Instantiate(playerPrefab, center + offset, rotation);
                player.SpawnAsPlayerObject(clientId, true);

                if (player.TryGetComponent(out PlayerIdentity identity) && MatchRegistry.TryGet(clientId, out ConnectionPayload info))
                    identity.ServerSet(info.Name, info.Team);
            }
        }
    }

    // ======================================================================
    // Hjælpere
    // ======================================================================

    private Player BuildPlayer() => new Player(
        id: AuthenticationService.Instance.PlayerId,
        data: new Dictionary<string, PlayerDataObject>
        {
            { PKeyName, new PlayerDataObject(PlayerDataObject.VisibilityOptions.Member, PlayerName) },
            { PKeyTeam, new PlayerDataObject(PlayerDataObject.VisibilityOptions.Member, MyTeam.ToString()) },
            { PKeyReady, new PlayerDataObject(PlayerDataObject.VisibilityOptions.Member, MyReady ? "1" : "0") }
        });

    private static Dictionary<string, DataObject> BuildLobbyData(MatchSettings s, int version) => new Dictionary<string, DataObject>
    {
        { KeyTeamSize, new DataObject(DataObject.VisibilityOptions.Public, s.TeamSize.ToString()) },
        { KeyHandSpeed, new DataObject(DataObject.VisibilityOptions.Public, s.HandSpeedPercent.ToString()) },
        { KeyLoot, new DataObject(DataObject.VisibilityOptions.Public, s.LootPercent.ToString()) },
        { KeySettingsVersion, new DataObject(DataObject.VisibilityOptions.Public, version.ToString()) }
    };

    private void ApplySettingsFromLobby(Lobby lobby)
    {
        Settings.MaxPlayers = lobby.MaxPlayers;
        Settings.TeamSize = ReadInt(lobby, KeyTeamSize, 1);
        Settings.HandSpeedPercent = ReadInt(lobby, KeyHandSpeed, 100);
        Settings.LootPercent = ReadInt(lobby, KeyLoot, 100);
    }

    /// <summary>Læs holdstørrelse fra en lobby (bruges af UI til listen over offentlige lobbyer).</summary>
    public static int ReadTeamSize(Lobby lobby) => ReadInt(lobby, KeyTeamSize, 1);

    private static int ReadInt(Lobby lobby, string key, int fallback)
    {
        if (lobby.Data != null && lobby.Data.TryGetValue(key, out DataObject d) && int.TryParse(d.Value, out int v))
            return v;
        return fallback;
    }

    /// <summary>Kører en handling, forhindrer dobbeltklik og viser fejl pænt.</summary>
    private async Task Run(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = "";
        try
        {
            await action();
        }
        catch (LobbyServiceException e)
        {
            ErrorMessage = TranslateLobbyError(e);
            Debug.LogWarning(e);
        }
        catch (RelayServiceException e)
        {
            ErrorMessage = "Relay-fejl: " + e.Message;
            Debug.LogWarning(e);
        }
        catch (Exception e)
        {
            ErrorMessage = e.Message;
            Debug.LogException(e);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string TranslateLobbyError(LobbyServiceException e)
    {
        switch (e.Reason)
        {
            case LobbyExceptionReason.LobbyNotFound: return "Lobbyen blev ikke fundet. Tjek koden.";
            case LobbyExceptionReason.LobbyFull: return "Lobbyen er fuld.";
            case LobbyExceptionReason.NoOpenLobbies: return "Der er ingen åbne offentlige lobbyer.";
            case LobbyExceptionReason.RateLimited: return "For mange forespørgsler. Vent et øjeblik og prøv igen.";
            default: return "Lobby-fejl: " + e.Message;
        }
    }
}