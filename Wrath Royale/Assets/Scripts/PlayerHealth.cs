using System;
using System.Collections;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

/// <summary>
/// Server-autoritativt liv og død. Kun serveren kan ændre liv (via TakeDamage).
/// Klienterne læser bare værdierne gennem NetworkVariables.
///
/// Kræver på player-prefab: CharacterController, NetworkObject, NetworkTransform (Owner),
/// NetworkPlayerMovement, PlayerCombat og en CapsuleCollider (Is Trigger) som hitbox.
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class PlayerHealth : NetworkBehaviour
{
    [Header("Liv")]
    [SerializeField] private float maxHealth = 100f;

    [Header("Hitbox")]
    [Tooltip("CapsuleCollider på spilleren med Is Trigger slået til. Det er denne, angreb rammer. " +
             "Størrelsen følger automatisk CharacterController (så den bliver lavere ved crouch).")]
    [SerializeField] private CapsuleCollider hitbox;

    [Header("Test")]
    [Tooltip("Respawner spilleren efter et par sekunder. Slå fra, når rigtige spilregler er på plads.")]
    [SerializeField] private bool debugRespawn = true;
    [SerializeField] private float respawnDelay = 3f;

    // Skrives kun af serveren, læses af alle
    private readonly NetworkVariable<float> currentHealth = new(100f);
    private readonly NetworkVariable<bool> isDead = new(false);

    public bool IsDead => isDead.Value;
    public float CurrentHealth => currentHealth.Value;
    public float MaxHealth => maxHealth;
    public CapsuleCollider Hitbox => hitbox;

    /// <summary>Kun på serveren: (offer, angriberens clientId). Bruges senere til drops og sejrsbetingelser.</summary>
    public event Action<PlayerHealth, ulong> Died;

    private CharacterController controller;
    private NetworkPlayerMovement movement;
    private PlayerCombat combat;
    private Vector3 spawnPosition;
    private GUIStyle labelStyle;

    private void Awake()
    {
        controller = GetComponent<CharacterController>();
        movement = GetComponent<NetworkPlayerMovement>();
        combat = GetComponent<PlayerCombat>();

        if (hitbox != null)
            hitbox.isTrigger = true;
    }

    public override void OnNetworkSpawn()
    {
        isDead.OnValueChanged += OnDeadChanged;

        if (IsServer)
        {
            currentHealth.Value = maxHealth;
            spawnPosition = transform.position;
        }

        ApplyDeadState(isDead.Value);
    }

    public override void OnNetworkDespawn()
    {
        isDead.OnValueChanged -= OnDeadChanged;
    }

    private void LateUpdate()
    {
        // Hitboxen følger CharacterControllerens størrelse (også på fremmede spillere,
        // hvor controlleren er slået fra men stadig får opdateret højde via crouch-synkronisering)
        if (hitbox == null || controller == null) return;
        hitbox.direction = 1; // Y-akse
        hitbox.radius = controller.radius;
        hitbox.height = controller.height;
        hitbox.center = controller.center;
    }

    // ---------------- Server: skade og død ----------------

    /// <summary>Må kun kaldes på serveren.</summary>
    public void TakeDamage(float amount, ulong attackerClientId)
    {
        if (!IsServer || isDead.Value || amount <= 0f) return;

        currentHealth.Value = Mathf.Max(0f, currentHealth.Value - amount);

        if (currentHealth.Value <= 0f)
            Die(attackerClientId);
    }

    private void Die(ulong killerClientId)
    {
        isDead.Value = true;
        Debug.Log($"[Server] Spiller {OwnerClientId} blev dræbt af spiller {killerClientId}");

        // Her hægter vi senere drop af våben/ofringer og sejrstjek på
        Died?.Invoke(this, killerClientId);

        if (debugRespawn)
            StartCoroutine(RespawnRoutine());
    }

    private IEnumerator RespawnRoutine()
    {
        yield return new WaitForSeconds(respawnDelay);
        if (!IsSpawned) yield break;

        currentHealth.Value = maxHealth;
        isDead.Value = false;
        RespawnRpc(spawnPosition);
    }

    // Ejeren styrer sin egen position, så det er ejeren, der skal flyttes
    [Rpc(SendTo.Owner)]
    private void RespawnRpc(Vector3 position)
    {
        if (movement != null) movement.ResetMotion();

        controller.enabled = false;
        transform.position = position;
        if (TryGetComponent(out NetworkTransform netTransform))
            netTransform.Teleport(position, transform.rotation, transform.localScale);
        controller.enabled = true;
    }

    // ---------------- Alle: visning af død tilstand ----------------

    private void OnDeadChanged(bool previous, bool now) => ApplyDeadState(now);

    private void ApplyDeadState(bool dead)
    {
        // Skjul kroppen og slå hitboxen fra, så en død spiller ikke kan rammes
        foreach (Renderer r in GetComponentsInChildren<Renderer>())
            r.enabled = !dead;

        if (hitbox != null)
            hitbox.enabled = !dead;

        // Ejeren mister styringen, mens han er død
        if (IsOwner)
        {
            if (movement != null) movement.enabled = !dead;
            if (combat != null) combat.enabled = !dead;
        }
    }

    // ---------------- HUD (kun ejeren) ----------------

    private void OnGUI()
    {
        if (!IsSpawned || !IsOwner) return;

        labelStyle ??= new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold };

        // Livsbjælke nederst til venstre
        float fill = maxHealth > 0f ? Mathf.Clamp01(currentHealth.Value / maxHealth) : 0f;
        Rect back = new Rect(20, Screen.height - 50, 250, 24);
        GUI.color = new Color(0f, 0f, 0f, 0.6f);
        GUI.DrawTexture(back, Texture2D.whiteTexture);
        GUI.color = Color.Lerp(Color.red, Color.green, fill);
        GUI.DrawTexture(new Rect(back.x + 2, back.y + 2, (back.width - 4) * fill, back.height - 4), Texture2D.whiteTexture);
        GUI.color = Color.white;
        GUI.Label(new Rect(back.xMax + 10, back.y - 4, 200, 32), $"{Mathf.CeilToInt(currentHealth.Value)} / {maxHealth}", labelStyle);

        if (isDead.Value)
            GUI.Label(new Rect(Screen.width / 2f - 100, Screen.height / 2f - 20, 300, 40), "DU ER DØD", labelStyle);
    }
}