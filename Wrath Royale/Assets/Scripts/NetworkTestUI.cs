using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;

/// <summary>
/// Simpel test-UI (IMGUI, kræver ingen Canvas) til at starte Host, Client eller Server.
/// Læg scriptet på samme GameObject som NetworkManager (eller et vilkårligt objekt i scenen).
/// </summary>
public class NetworkTestUI : MonoBehaviour
{
    [SerializeField] private string ipAddress = "127.0.0.1";
    [SerializeField] private ushort port = 7777;

    private void OnGUI()
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null)
        {
            GUILayout.BeginArea(new Rect(10, 10, 300, 100));
            GUILayout.Label("Ingen NetworkManager i scenen!");
            GUILayout.EndArea();
            return;
        }

        GUILayout.BeginArea(new Rect(10, 10, 300, 300));

        if (!nm.IsClient && !nm.IsServer)
        {
            // Ikke forbundet endnu
            GUILayout.Label("IP-adresse (kun til Client):");
            ipAddress = GUILayout.TextField(ipAddress);

            ApplyConnectionData(nm);

            if (GUILayout.Button("Start Host", GUILayout.Height(35)))
                nm.StartHost();

            if (GUILayout.Button("Start Client", GUILayout.Height(35)))
                nm.StartClient();

            if (GUILayout.Button("Start Server (uden spiller)", GUILayout.Height(35)))
                nm.StartServer();
        }
        else
        {
            // Forbundet
            string mode = nm.IsHost ? "Host" : nm.IsServer ? "Server" : "Client";
            GUILayout.Label($"Tilstand: {mode}");
            GUILayout.Label($"Forbundne spillere: {nm.ConnectedClientsIds.Count}");

            if (GUILayout.Button("Afbryd", GUILayout.Height(30)))
                nm.Shutdown();

            GUILayout.Label("(Tryk Escape for at frigive musen)");
        }

        GUILayout.EndArea();
    }

    private void ApplyConnectionData(NetworkManager nm)
    {
        if (nm.TryGetComponent(out UnityTransport transport))
            transport.SetConnectionData(ipAddress, port);
    }
}