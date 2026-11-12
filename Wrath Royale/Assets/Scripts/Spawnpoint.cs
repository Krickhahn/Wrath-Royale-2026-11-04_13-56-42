using UnityEngine;

/// <summary>
/// Markerer et sted, hvor en spiller eller et helt hold kan starte.
/// Læg flere af dem rundt på banen i spilscenen, gerne i nogenlunde samme højde.
/// Placér objektet i jordhøjde (spillerens fødder). Et hold spawner samlet omkring ét punkt.
/// </summary>
public class SpawnPoint : MonoBehaviour
{
    private void OnDrawGizmos()
    {
        Gizmos.color = new Color(0.2f, 0.9f, 0.4f, 0.9f);
        Gizmos.DrawWireSphere(transform.position + Vector3.up * 0.1f, 1.5f);
        Gizmos.DrawLine(transform.position, transform.position + Vector3.up * 1.8f);
        Gizmos.DrawLine(transform.position + Vector3.up * 1.0f, transform.position + Vector3.up * 1.0f + transform.forward);
    }
}