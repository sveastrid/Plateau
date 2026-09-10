using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Lets any player in the room move everyone to a different game.
///
/// Lives on Player.prefab. Netcode's scene manager (NetworkConfig.EnableSceneManagement = 1)
/// does the actual work: the server calls LoadScene, every client loads it, and spawned
/// NetworkObjects — the players — are carried across. Clients must never call
/// UnityEngine.SceneManagement.SceneManager.LoadScene while a session is running.
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class GameSelector : NetworkBehaviour
{
    // Server-side. Stops a second press — or two players pressing at the same moment — from
    // stacking two scene loads. Cleared when the load finishes or times out
    // (NetworkConfig.LoadSceneTimeOut is 120s), so it cannot wedge permanently.
    static bool s_SwitchInProgress;

    /// <summary>Called on the local client by MenuControl. Safe to call from anyone.</summary>
    public void RequestGame(string gameKey)
    {
        if (!IsOwner)
        {
            return;
        }
        RequestGameServerRpc(gameKey);
    }

    [ServerRpc]
    void RequestGameServerRpc(string gameKey, ServerRpcParams rpcParams = default)
    {
        // Never hand a client-supplied string to LoadScene. Only keys in GameRoutes are legal.
        if (!GameRoutes.TryGetScene(gameKey, out string sceneName))
        {
            Debug.LogWarning("GameSelector: client " + rpcParams.Receive.SenderClientId +
                             " asked for unknown game '" + gameKey + "'.");
            return;
        }

        // A public room plays one game. The menu already shows only that game, but the menu filter
        // is cosmetic — this is the enforcement, and it goes on the next line after the GameRoutes
        // check for exactly the reason that check exists.
        string locked = RoomAnchor.LockedGameKey;
        if (locked != null && locked != gameKey)
        {
            Debug.LogWarning("GameSelector: client " + rpcParams.Receive.SenderClientId +
                             " asked for '" + gameKey + "' in a public room locked to '" +
                             locked + "'.");
            return;
        }

        // Nobody in the room owns it. Same argument: MenuControl filters the rows it builds, but a
        // key name is a client-supplied string and the filter is not what makes it safe.
        if (!StoreService.MaskAllows(RoomLibrary.Union(), gameKey))
        {
            Debug.LogWarning("GameSelector: client " + rpcParams.Receive.SenderClientId +
                             " asked for '" + gameKey + "', which nobody in the room owns.");
            return;
        }

        // Before the load, not after: picking the game the room is already in is allowed to reset
        // it, and LoadGameScene below deliberately does nothing in that case, so a scene-load hook
        // would never fire for Chasms -> Chasms.
        //
        // Resolved by key, not by active scene — the room is still in the *previous* game here, and
        // the game being selected has almost certainly not loaded its board yet. A session that
        // wants to reset has to latch and act later; see IGameSession.OnGameSelected.
        IGameSession session = GameSessionRegistry.ForKey(gameKey);
        if (session != null)
        {
            session.OnGameSelected();
        }

        LoadGameScene(sceneName);
    }

    /// <summary>
    /// Server only. The one place in the project that changes the shared scene.
    /// Also called by GameController when the host first opens the room.
    /// </summary>
    public static void LoadGameScene(string sceneName)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsServer || nm.SceneManager == null)
        {
            return;
        }

        if (s_SwitchInProgress)
        {
            Debug.Log("GameSelector: ignoring '" + sceneName + "', a scene load is already running.");
            return;
        }

        // Picking the game the room is already in is a no-op, not a restart. See §5.
        if (SceneManager.GetActiveScene().name == sceneName)
        {
            Debug.Log("GameSelector: the room is already in '" + sceneName + "'.");
            return;
        }

        nm.SceneManager.OnLoadEventCompleted += HandleLoadEventCompleted;
        s_SwitchInProgress = true;

        SceneEventProgressStatus status = nm.SceneManager.LoadScene(sceneName, LoadSceneMode.Single);
        if (status != SceneEventProgressStatus.Started)
        {
            s_SwitchInProgress = false;
            nm.SceneManager.OnLoadEventCompleted -= HandleLoadEventCompleted;

            // InvalidSceneName almost always means the scene is missing from the build's
            // scene list (Step 3). It fails silently otherwise.
            Debug.LogError("GameSelector: LoadScene(\"" + sceneName + "\") returned " + status + ".");
        }
    }

    static void HandleLoadEventCompleted(string sceneName, LoadSceneMode mode,
                                         List<ulong> clientsCompleted, List<ulong> clientsTimedOut)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm != null && nm.SceneManager != null)
        {
            nm.SceneManager.OnLoadEventCompleted -= HandleLoadEventCompleted;
        }

        s_SwitchInProgress = false;

        if (clientsTimedOut != null && clientsTimedOut.Count > 0)
        {
            Debug.LogWarning("GameSelector: " + clientsTimedOut.Count +
                             " client(s) timed out loading '" + sceneName + "'.");
        }
    }
}
