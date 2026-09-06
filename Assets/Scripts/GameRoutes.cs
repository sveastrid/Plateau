using UnityEngine;

/// <summary>
/// The room's game list: menu key -> scene name.
///
/// This used to be a Dictionary literal with a const pair per game, so adding a game meant editing
/// a file the other games compile against. The rows now live in Assets/Resources/GameCatalog.asset
/// as <see cref="GameModule"/> assets; this stays as the lookup every caller already knows, so
/// nothing else had to move.
///
/// Adding a game to this project means: a scene, a GameModule asset, a row in the catalog, and the
/// scene in the build list. No shared source file, and neither menu prefab.
/// </summary>
public static class GameRoutes
{
    /// <summary>
    /// The scene every room starts in. GameController loads this once, when the host opens the room.
    /// </summary>
    public static string DefaultScene
    {
        get
        {
            GameCatalog catalog = GameCatalog.Instance;

            if (catalog == null || catalog.defaultGame == null)
            {
                Debug.LogError("GameRoutes: the catalog has no default game, so the host has " +
                               "nowhere to open the room. Set 'Default Game' on " +
                               "Resources/GameCatalog.asset.");
                return "";
            }

            return catalog.defaultGame.sceneName;
        }
    }

    public static bool IsGameKey(string keyName)
    {
        GameCatalog catalog = GameCatalog.Instance;
        return catalog != null && catalog.ByKey(keyName) != null;
    }

    /// <summary>
    /// Is this scene one of the games, as opposed to the lobby? Read by CameraController2 — the
    /// lobby has an XRRig too, and its layout is the keyboard in front of the user, so the
    /// board-side player ring must not be applied there — and by MenuControl, which leaves the
    /// pointer on in the lobby because the keyboard *is* the interaction there.
    /// </summary>
    public static bool IsGameScene(string sceneName)
    {
        GameCatalog catalog = GameCatalog.Instance;
        return catalog != null && catalog.ByScene(sceneName) != null;
    }

    /// <summary>
    /// The scene behind a menu key. False for anything not in the catalog — which is what makes it
    /// safe for GameSelector to hand the result to LoadScene, since the key came from a client.
    /// </summary>
    public static bool TryGetScene(string gameKey, out string sceneName)
    {
        GameCatalog catalog = GameCatalog.Instance;
        GameModule module = catalog != null ? catalog.ByKey(gameKey) : null;

        sceneName = module != null ? module.sceneName : null;
        return module != null;
    }
}
