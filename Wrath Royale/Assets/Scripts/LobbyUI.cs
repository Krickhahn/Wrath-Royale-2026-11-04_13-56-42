using Unity.Services.Lobbies.Models;
using UnityEngine;

/// <summary>
/// Simpel lobby-skærm (IMGUI, kræver ingen Canvas). Læg den på samme GameObject som LobbyManager.
/// Viser menu, lobby med hold/klar/indstillinger og en "Forlad spil"-knap under kampen.
/// </summary>
[RequireComponent(typeof(LobbyManager))]
public class LobbyUI : MonoBehaviour
{
    private const float PanelWidth = 760f;
    private const float PanelHeight = 620f;

    private string joinCode = "";
    private int maxPlayers = 8;
    private Vector2 lobbyListScroll;
    private Vector2 playerListScroll;

    private GUIStyle label, header, button, textField, error, smallLabel;

    private void OnGUI()
    {
        LobbyManager m = LobbyManager.Instance;
        if (m == null) return;

        if (m.State == LobbyState.InGame)
        {
            DrawInGame(m);
            return;
        }

        EnsureStyles();

        // Tegn i et virtuelt 1280x720-lærred, så teksten er læsbar på alle opløsninger
        float scale = Mathf.Max(0.5f, Screen.height / 720f);
        GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
        float w = Screen.width / scale;
        float h = Screen.height / scale;

        Rect panel = new Rect((w - PanelWidth) / 2f, (h - PanelHeight) / 2f, PanelWidth, PanelHeight);
        GUILayout.BeginArea(panel, GUI.skin.box);
        GUILayout.Space(8);

        switch (m.State)
        {
            case LobbyState.Initializing: DrawInitializing(m); break;
            case LobbyState.Menu: DrawMenu(m); break;
            case LobbyState.InLobby: DrawLobby(m); break;
            case LobbyState.Starting: DrawStarting(m); break;
        }

        GUILayout.EndArea();
        GUI.matrix = Matrix4x4.identity;
    }

    // ======================================================================
    // Skærme
    // ======================================================================

    private void DrawInitializing(LobbyManager m)
    {
        GUILayout.Label("Forbinder til Unity Services...", header);

        if (!string.IsNullOrEmpty(m.ErrorMessage))
        {
            GUILayout.Label(m.ErrorMessage, error);
            if (GUILayout.Button("Prøv igen", button, GUILayout.Height(36)))
                m.Retry();
        }
    }

    private void DrawMenu(LobbyManager m)
    {
        GUILayout.Label("Lobby", header);

        // Navn
        GUILayout.BeginHorizontal();
        GUILayout.Label("Dit navn:", label, GUILayout.Width(110));
        m.PlayerName = GUILayout.TextField(m.PlayerName ?? "", 12, textField, GUILayout.Width(220));
        GUILayout.EndHorizontal();

        GUILayout.Space(8);
        GUI.enabled = !m.IsBusy;

        // Opret
        GUILayout.BeginHorizontal();
        GUILayout.Label($"Max spillere: {maxPlayers}", label, GUILayout.Width(170));
        maxPlayers = Mathf.RoundToInt(GUILayout.HorizontalSlider(maxPlayers, 2, 100, GUILayout.Width(200)));
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Opret offentlig lobby", button, GUILayout.Height(36)))
            m.CreateLobby(false, maxPlayers);
        if (GUILayout.Button("Opret privat lobby", button, GUILayout.Height(36)))
            m.CreateLobby(true, maxPlayers);
        GUILayout.EndHorizontal();

        GUILayout.Space(8);

        // Join med kode
        GUILayout.BeginHorizontal();
        GUILayout.Label("Lobbykode:", label, GUILayout.Width(110));
        joinCode = GUILayout.TextField(joinCode, 6, textField, GUILayout.Width(140)).ToUpperInvariant();
        GUI.enabled = !m.IsBusy && joinCode.Length > 0;
        if (GUILayout.Button("Join med kode", button, GUILayout.Height(32)))
            m.JoinByCode(joinCode);
        GUI.enabled = !m.IsBusy;
        if (GUILayout.Button("Quick join", button, GUILayout.Height(32)))
            m.QuickJoin();
        GUILayout.EndHorizontal();

        GUILayout.Space(8);

        // Liste over offentlige lobbyer
        GUILayout.BeginHorizontal();
        GUILayout.Label("Offentlige lobbyer", header);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Opdater", button, GUILayout.Width(100), GUILayout.Height(28)))
            m.RefreshPublicLobbies();
        GUILayout.EndHorizontal();

        lobbyListScroll = GUILayout.BeginScrollView(lobbyListScroll, GUILayout.Height(190));
        if (m.PublicLobbies.Count == 0)
        {
            GUILayout.Label("Ingen offentlige lobbyer lige nu. Opret en selv!", smallLabel);
        }
        else
        {
            foreach (Lobby lobby in m.PublicLobbies)
            {
                GUILayout.BeginHorizontal();
                int teamSize = LobbyManager.ReadTeamSize(lobby);
                string mode = teamSize > 1 ? $"hold på {teamSize}" : "solo";
                GUILayout.Label($"{lobby.Name}  ({lobby.Players.Count}/{lobby.MaxPlayers}, {mode})", label);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Join", button, GUILayout.Width(80), GUILayout.Height(28)))
                    m.JoinById(lobby.Id);
                GUILayout.EndHorizontal();
            }
        }
        GUILayout.EndScrollView();

        GUI.enabled = true;
        DrawError(m);
    }

    private void DrawLobby(LobbyManager m)
    {
        Lobby lobby = m.CurrentLobby;
        if (lobby == null) return;

        // Titel og kode
        GUILayout.BeginHorizontal();
        GUILayout.Label($"{lobby.Name}  ({(lobby.IsPrivate ? "privat" : "offentlig")})", header);
        GUILayout.FlexibleSpace();
        GUILayout.Label($"Kode: {lobby.LobbyCode}", header);
        if (GUILayout.Button("Kopiér", button, GUILayout.Width(80), GUILayout.Height(28)))
            GUIUtility.systemCopyBuffer = lobby.LobbyCode;
        GUILayout.EndHorizontal();

        GUILayout.Label($"Spillere: {lobby.Players.Count}/{lobby.MaxPlayers}", smallLabel);

        // Spillerliste
        playerListScroll = GUILayout.BeginScrollView(playerListScroll, GUI.skin.box, GUILayout.Height(150));
        foreach (Player p in lobby.Players)
        {
            bool isHostPlayer = p.Id == lobby.HostId;
            bool isMe = p.Id == m.MyPlayerId;
            int team = LobbyManager.GetTeam(p);

            GUILayout.BeginHorizontal();
            GUILayout.Label((isHostPlayer ? "★ " : "") + LobbyManager.GetName(p) + (isMe ? " (dig)" : ""), label, GUILayout.Width(300));
            GUILayout.Label(m.Settings.HasTeams ? (team > 0 ? $"Hold {team}" : "Intet hold") : "", label, GUILayout.Width(110));
            GUILayout.Label(LobbyManager.IsReady(p) ? "✔ Klar" : "…", label, GUILayout.Width(80));
            GUILayout.FlexibleSpace();
            if (m.IsHost && !isMe && GUILayout.Button("Smid ud", button, GUILayout.Width(90), GUILayout.Height(24)))
                m.KickPlayer(p.Id);
            GUILayout.EndHorizontal();
        }
        GUILayout.EndScrollView();

        // Valg af hold
        if (m.Settings.HasTeams)
        {
            GUILayout.Label($"Vælg hold (max {m.Settings.TeamSize} pr. hold):", label);
            DrawTeamButtons(m);
        }

        GUILayout.Space(6);
        DrawSettings(m);
        GUILayout.FlexibleSpace();

        // Knapper i bunden
        string startReason = "";
        bool canStart = m.IsHost && m.CanStart(out startReason);

        GUILayout.BeginHorizontal();
        GUI.enabled = true;
        if (GUILayout.Button(m.MyReady ? "Ikke klar" : "Klar", button, GUILayout.Height(40)))
            m.ToggleReady();

        if (m.IsHost)
        {
            GUI.enabled = canStart && !m.IsBusy;
            if (GUILayout.Button("Start spil", button, GUILayout.Height(40)))
                m.StartGame();
            GUI.enabled = true;
        }

        if (GUILayout.Button("Forlad lobby", button, GUILayout.Height(40)))
            m.LeaveLobby();
        GUILayout.EndHorizontal();

        if (m.IsHost && !canStart && !string.IsNullOrEmpty(startReason))
            GUILayout.Label(startReason, smallLabel);

        DrawError(m);
    }

    private void DrawTeamButtons(LobbyManager m)
    {
        int teams = m.NumberOfTeams;
        const int perRow = 8;

        for (int rowStart = 1; rowStart <= teams; rowStart += perRow)
        {
            GUILayout.BeginHorizontal();
            for (int t = rowStart; t < rowStart + perRow && t <= teams; t++)
            {
                int count = m.TeamMemberCount(t);
                bool mine = m.MyTeam == t;
                bool full = !mine && count >= m.Settings.TeamSize;

                GUI.enabled = !full;
                GUI.color = mine ? new Color(0.5f, 1f, 0.5f) : Color.white;
                if (GUILayout.Button($"{t} ({count}/{m.Settings.TeamSize})", button, GUILayout.Height(28)))
                    m.SetTeam(t);
                GUI.color = Color.white;
                GUI.enabled = true;
            }
            GUILayout.EndHorizontal();
        }
    }

    private void DrawSettings(LobbyManager m)
    {
        GUILayout.Label(m.IsHost ? "Indstillinger" : "Indstillinger (bestemmes af værten)", header);

        string teamText = m.Settings.TeamSize == 1 ? "Solo (alle mod alle)" : $"Hold på {m.Settings.TeamSize}";
        DrawSettingRow(m, "Spiltype:", teamText, m.ChangeTeamSize);
        DrawSettingRow(m, "Hånd-hastighed:", m.Settings.HandSpeedPercent + "%", m.ChangeHandSpeed);
        DrawSettingRow(m, "Loot-mængde:", m.Settings.LootPercent + "%", m.ChangeLoot);
    }

    private void DrawSettingRow(LobbyManager m, string name, string value, System.Action<int> change)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(name, label, GUILayout.Width(170));

        GUI.enabled = m.IsHost;
        if (GUILayout.Button("−", button, GUILayout.Width(36), GUILayout.Height(26))) change(-1);
        GUILayout.Label(value, label, GUILayout.Width(190));
        if (GUILayout.Button("+", button, GUILayout.Width(36), GUILayout.Height(26))) change(1);
        GUI.enabled = true;

        GUILayout.EndHorizontal();
    }

    private void DrawStarting(LobbyManager m)
    {
        GUILayout.Label("Starter spillet...", header);
        GUILayout.Label(m.StatusMessage, label);
        DrawError(m);
    }

    private void DrawInGame(LobbyManager m)
    {
        // Lille knap, når musen er frigivet (Escape), så man kan forlade kampen
        if (Cursor.lockState == CursorLockMode.Locked) return;

        EnsureStyles();
        if (GUI.Button(new Rect(Screen.width - 180, 10, 170, 36), "Forlad spil", button))
            m.LeaveGame();
    }

    private void DrawError(LobbyManager m)
    {
        if (!string.IsNullOrEmpty(m.ErrorMessage))
        {
            GUILayout.Space(4);
            GUILayout.Label(m.ErrorMessage, error);
        }
    }

    // ======================================================================
    // Stilarter
    // ======================================================================

    private void EnsureStyles()
    {
        if (label != null) return;

        label = new GUIStyle(GUI.skin.label) { fontSize = 16, wordWrap = true };
        smallLabel = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true };
        header = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold };
        button = new GUIStyle(GUI.skin.button) { fontSize = 16 };
        textField = new GUIStyle(GUI.skin.textField) { fontSize = 16 };
        error = new GUIStyle(GUI.skin.label) { fontSize = 14, wordWrap = true };
        error.normal.textColor = new Color(1f, 0.4f, 0.4f);
    }
}