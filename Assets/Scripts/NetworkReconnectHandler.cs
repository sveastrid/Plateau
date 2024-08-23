using UnityEngine;
using Unity.Netcode;
using System.Collections;

public class NetworkReconnectHandler : MonoBehaviour
{
    private bool isReconnecting = false;

    void Update()
    {
        if (NetworkManager.Singleton.IsClient && !NetworkManager.Singleton.IsConnectedClient && !isReconnecting)
        {
            isReconnecting = true;
            StartCoroutine(TryReconnect());
        }
    }

    private IEnumerator TryReconnect()
    {
        while (!NetworkManager.Singleton.IsConnectedClient)
        {
            Debug.Log("Trying to reconnect");
            NetworkManager.Singleton.Shutdown();
            NetworkManager.Singleton.StartClient();
            yield return new WaitForSeconds(5); // Wait for 5 seconds before trying again
        }
        isReconnecting = false;
    }
}