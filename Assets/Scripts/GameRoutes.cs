using System.Collections.Generic;

/// <summary>
/// The room's game list: menu key -> scene name.
///
/// Adding a game to this project should mean adding a key to Menu1.prefab, a case to
/// MenuControl.HandleKey, a scene to the build list, and a row here. Nothing else.
/// The key strings must match keyInfo.keyName on the menu keys exactly — pointerControl
/// reports keyName, not the visible label (pointerControl.cs:28).
/// </summary>
public static class GameRoutes
{
    /// <summary>The game every room starts in.</summary>
    public const string DefaultGameKey = "Stairs";

    /// <summary>
    /// The Plateau board game. Named here rather than as a literal in PlateauGame so the key and
    /// the scene cannot drift apart from the row below.
    /// </summary>
    public const string PlateauGameKey = "Chasms";
    public const string PlateauSceneName = "ChasmGame";

    /// <summary>
    /// BASH — four bases, four gamepieces each, and a trail of geometry you steer into people.
    /// Named here for the same reason Chasms is: so MenuControl's key and the scene name cannot
    /// drift apart from the row below.
    /// </summary>
    public const string BashGameKey = "BASH";
    public const string BashSceneName = "BashGame";

    static readonly Dictionary<string, string> SceneByKey = new Dictionary<string, string>
    {
        { "Stairs", "StairsGame" },
        { PlateauGameKey, PlateauSceneName },
        { BashGameKey, BashSceneName },
    };

    public static string DefaultScene => SceneByKey[DefaultGameKey];

    public static bool IsGameKey(string keyName) => SceneByKey.ContainsKey(keyName ?? "");

    /// <summary>
    /// Is this scene one of the games, as opposed to the lobby? Read by CameraController2:
    /// the lobby has an XRRig too, and its layout is the keyboard in front of the user, so the
    /// board-side player ring must not be applied there.
    /// </summary>
    public static bool IsGameScene(string sceneName) => SceneByKey.ContainsValue(sceneName ?? "");

    public static bool TryGetScene(string gameKey, out string sceneName) =>
        SceneByKey.TryGetValue(gameKey ?? "", out sceneName);
}
