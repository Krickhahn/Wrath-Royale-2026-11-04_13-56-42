using System;
using System.Collections.Generic;

/// <summary>Spilindstillinger, som lobbyens vært vælger, og som alle spillere kan se.</summary>
[Serializable]
public class MatchSettings
{
    /// <summary>Antal spillere pr. hold. 1 = alle mod alle (ingen hold).</summary>
    public int TeamSize = 1;

    /// <summary>Hvor hurtigt hånden presser ned, i procent (100 = normal).</summary>
    public int HandSpeedPercent = 100;

    /// <summary>Mængden af loot på banen, i procent (100 = normal).</summary>
    public int LootPercent = 100;

    public int MaxPlayers = 8;

    public bool HasTeams => TeamSize > 1;

    public MatchSettings Clone() => (MatchSettings)MemberwiseClone();
}

/// <summary>Sendes fra hver spiller til serveren, når de forbinder (via Connection Approval).</summary>
[Serializable]
public struct ConnectionPayload
{
    public string PlayerId;
    public string Name;
    public int Team;
}

/// <summary>
/// Samler oplysninger om den igangværende kamp. Serveren udfylder Players,
/// når spillere forbinder. Settings sættes hos alle ved kampstart.
/// </summary>
public static class MatchRegistry
{
    public static MatchSettings Settings = new MatchSettings();

    /// <summary>Kun gyldig på serveren: clientId -> spillerens navn og hold.</summary>
    public static readonly Dictionary<ulong, ConnectionPayload> Players = new Dictionary<ulong, ConnectionPayload>();

    public static void Clear()
    {
        Players.Clear();
        Settings = new MatchSettings();
    }

    public static bool TryGet(ulong clientId, out ConnectionPayload info) => Players.TryGetValue(clientId, out info);

    /// <summary>Serverside: er de to spillere på samme hold? (Solo-spillere har hold 0 og er aldrig holdkammerater.)</summary>
    public static bool AreTeammates(ulong clientA, ulong clientB)
    {
        if (clientA == clientB) return true;
        return Players.TryGetValue(clientA, out ConnectionPayload a)
            && Players.TryGetValue(clientB, out ConnectionPayload b)
            && a.Team > 0 && a.Team == b.Team;
    }
}