using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// Spillerens navn og hold, synkroniseret til alle. Sættes af serveren ved spawn (LobbyManager).
/// Læg dette script på player-prefaben. Bruges senere til bl.a. navneskilte, holdkammerat-tjek
/// og friendly fire.
/// </summary>
public class PlayerIdentity : NetworkBehaviour
{
    private readonly NetworkVariable<FixedString32Bytes> playerName = new();
    private readonly NetworkVariable<int> team = new();

    public string PlayerName => playerName.Value.ToString();

    /// <summary>0 = solo (ingen hold).</summary>
    public int Team => team.Value;

    public bool IsTeammateOf(PlayerIdentity other)
        => other != null && (other == this || (Team > 0 && Team == other.Team));

    /// <summary>Kun serveren.</summary>
    public void ServerSet(string displayName, int teamNumber)
    {
        if (!IsServer) return;

        if (string.IsNullOrEmpty(displayName)) displayName = "Spiller";
        if (displayName.Length > 12) displayName = displayName.Substring(0, 12);

        playerName.Value = new FixedString32Bytes(displayName);
        team.Value = teamNumber;
    }
}