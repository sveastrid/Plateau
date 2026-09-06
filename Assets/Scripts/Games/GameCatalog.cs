using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// The room's game list. One row per <see cref="GameModule"/>, and the only thing that has to
/// change when a game is added.
///
/// Lives at Assets/Resources/GameCatalog.asset so it can be loaded without a serialized reference
/// from anywhere — GameRoutes is a static class and MenuControl lives on PersistentRig, neither of
/// which has an Inspector slot a scene could fill.
/// </summary>
[CreateAssetMenu(fileName = "GameCatalog", menuName = "MR Board Game/Game Catalog")]
public class GameCatalog : ScriptableObject
{
    /// <summary>Path passed to Resources.Load. The asset must sit directly in a Resources folder.</summary>
    public const string ResourceName = "GameCatalog";

    [Tooltip("Every game the room can switch into, in the order their keys appear in the menu.")]
    public List<GameModule> games = new List<GameModule>();

    [Tooltip("The game every room starts in, when the host first opens it.")]
    public GameModule defaultGame;

    static GameCatalog s_instance;
    static bool s_loadFailed;

    /// <summary>
    /// The loaded catalog, or null when the asset is missing. Null is reported once rather than
    /// every frame — GameRoutes.IsGameScene is called from ApplyPointerDefault and PlaceAtRingSlot,
    /// so a missing asset would otherwise be a log flood on top of a broken room.
    /// </summary>
    public static GameCatalog Instance
    {
        get
        {
            if (s_instance != null)
            {
                return s_instance;
            }

            s_instance = Resources.Load<GameCatalog>(ResourceName);

            if (s_instance == null && !s_loadFailed)
            {
                s_loadFailed = true;
                Debug.LogError("GameCatalog: no Resources/" + ResourceName + ".asset. No game can " +
                               "be reached from the menu until it exists. Create it with " +
                               "Assets > Create > MR Board Game > Game Catalog.");
            }

            return s_instance;
        }
    }

    /// <summary>The module for a menu key, or null. A null key is not an error — it is "no game".</summary>
    public GameModule ByKey(string gameKey)
    {
        if (string.IsNullOrEmpty(gameKey))
        {
            return null;
        }

        for (int i = 0; i < games.Count; i++)
        {
            if (games[i] != null && games[i].gameKey == gameKey)
            {
                return games[i];
            }
        }

        return null;
    }

    /// <summary>The module whose scene this is, or null for the lobby.</summary>
    public GameModule ByScene(string sceneName)
    {
        if (string.IsNullOrEmpty(sceneName))
        {
            return null;
        }

        for (int i = 0; i < games.Count; i++)
        {
            if (games[i] != null && games[i].sceneName == sceneName)
            {
                return games[i];
            }
        }

        return null;
    }

    /// <summary>
    /// The game currently loaded, or null in the lobby.
    ///
    /// This reads SceneManager.GetActiveScene(), never gameObject.scene: every object that would
    /// ask is on PersistentRig or the Network Manager, and those report the fixed pseudo-scene
    /// "DontDestroyOnLoad" for the life of the app.
    /// </summary>
    public static GameModule ActiveModule
    {
        get
        {
            GameCatalog catalog = Instance;
            return catalog != null ? catalog.ByScene(SceneManager.GetActiveScene().name) : null;
        }
    }
}
