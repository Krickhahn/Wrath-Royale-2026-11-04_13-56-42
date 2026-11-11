using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>Data for ét angreb. Våben kommer senere til at levere deres egne værdier.</summary>
[Serializable]
public struct AttackData
{
    public float damage;
    public float range;
    [Tooltip("Halv vinkel i grader omkring sigtelinjen, hvor et mål kan rammes.")]
    public float coneAngle;
    public float cooldown;
}

/// <summary>
/// Næve-angreb. Ejeren sender kun et ønske om at angribe (med sigtepunkt og retning).
/// Serveren tjekker cooldown og position og afgør selv, hvem der bliver ramt og hvor meget skade der gives.
///
/// Styring: venstre museknap (hold nede for at slå gentagne gange).
/// </summary>
[RequireComponent(typeof(PlayerHealth))]
public class PlayerCombat : NetworkBehaviour
{
    [Header("Sigte")]
    [Tooltip("Kameraet (eller CameraPivot). Angrebet sigter ud fra dets position og retning.")]
    [SerializeField] private Transform aimOrigin;

    [Header("Næver")]
    [SerializeField]
    private AttackData fists = new AttackData
    {
        damage = 10f,
        range = 2f,
        coneAngle = 35f,
        cooldown = 0.5f
    };

    [Header("Validering (server)")]
    [Tooltip("Hvor langt fra spillerens position sigtepunktet må være. Skal være lidt generøst pga. netværksforsinkelse.")]
    [SerializeField] private float maxOriginError = 3f;

    private static readonly Collider[] overlapBuffer = new Collider[32];

    private PlayerHealth health;
    private float nextLocalAttack;
    private float nextServerAttack;
    private float hitMarkerUntil;

    private void Awake()
    {
        health = GetComponent<PlayerHealth>();
    }

    // ---------------- Klient: input ----------------

    private void Update()
    {
        if (!IsSpawned || !IsOwner || health.IsDead) return;
        if (Cursor.lockState != CursorLockMode.Locked) return;

        Mouse mouse = Mouse.current;
        if (mouse == null || aimOrigin == null) return;

        if (mouse.leftButton.isPressed && Time.time >= nextLocalAttack)
        {
            nextLocalAttack = Time.time + fists.cooldown;
            AttackRpc(aimOrigin.position, aimOrigin.forward);
        }
    }

    // ---------------- Server: validering og hit-detektion ----------------

    [Rpc(SendTo.Server, RequireOwnership = true)]
    private void AttackRpc(Vector3 origin, Vector3 direction)
    {
        if (health.IsDead) return;
        if (!IsFinite(origin) || !IsFinite(direction) || direction.sqrMagnitude < 0.01f) return;

        // Cooldown (lidt tolerance så netværksjitter ikke afviser lovlige angreb)
        if (Time.time < nextServerAttack) return;
        nextServerAttack = Time.time + fists.cooldown * 0.8f;

        // Sigtepunktet skal ligge tæt på spillerens position, som serveren kender
        Vector3 bodyCenter = transform.position + Vector3.up;
        if (Vector3.Distance(origin, bodyCenter) > maxOriginError) return;

        if (TryMelee(origin, direction.normalized, fists))
            HitConfirmRpc();
    }

    private bool TryMelee(Vector3 origin, Vector3 direction, AttackData attack)
    {
        int count = Physics.OverlapSphereNonAlloc(origin, attack.range, overlapBuffer, ~0, QueryTriggerInteraction.Collide);

        PlayerHealth bestTarget = null;
        float bestDistance = float.MaxValue;

        for (int i = 0; i < count; i++)
        {
            Collider col = overlapBuffer[i];
            PlayerHealth target = col.GetComponentInParent<PlayerHealth>();

            // Kun andre, levende spilleres hitbox tæller
            if (target == null || target == health || target.IsDead) continue;
            if (col != target.Hitbox) continue;

            // Nærmeste punkt på målet, og om det ligger inden for sigtekeglen
            Vector3 closest = col.ClosestPoint(origin);
            Vector3 toTarget = closest - origin;
            float distance = toTarget.magnitude;

            if (distance > attack.range) continue;
            if (distance > 0.05f && Vector3.Angle(direction, toTarget) > attack.coneAngle) continue;
            if (distance >= bestDistance) continue;

            // Vægge blokerer. (Andre spillere blokerer ikke, så vi ikke fejlagtigt afviser et ramt mål.)
            if (Physics.Linecast(origin, closest, out RaycastHit block, ~0, QueryTriggerInteraction.Ignore)
                && block.collider.GetComponentInParent<PlayerHealth>() == null)
                continue;

            bestTarget = target;
            bestDistance = distance;
        }

        if (bestTarget == null) return false;

        bestTarget.TakeDamage(attack.damage, OwnerClientId);
        return true;
    }

    // ---------------- Feedback til angriberen ----------------

    [Rpc(SendTo.Owner)]
    private void HitConfirmRpc()
    {
        hitMarkerUntil = Time.time + 0.15f;
    }

    private void OnGUI()
    {
        if (!IsSpawned || !IsOwner || health.IsDead) return;

        // Sigtekorn: hvidt, og rødt et øjeblik når man rammer
        bool hit = Time.time < hitMarkerUntil;
        float size = hit ? 10f : 4f;
        GUI.color = hit ? Color.red : Color.white;
        GUI.DrawTexture(new Rect(Screen.width / 2f - size / 2f, Screen.height / 2f - size / 2f, size, size), Texture2D.whiteTexture);
        GUI.color = Color.white;
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
}