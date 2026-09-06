using UnityEngine;

/// <summary>
/// One game, described as data rather than as rows in shared source files.
///
/// Adding a game used to mean editing GameRoutes, three separate places in MenuControl, and both
/// menu prefabs — every one of them a file the other games also depend on, so a typo in a new game
/// broke the existing ones. A module asset carries the same facts without any of that: the catalog
/// finds it, MenuControl builds a key from it, and no shared file learns the game's name.
///
/// Create one with Assets > Create > MR Board Game > Game Module, then add it to
/// Assets/Resources/GameCatalog.asset. GameCatalogValidator complains if you forget the second step.
/// </summary>
[CreateAssetMenu(fileName = "GameModule", menuName = "MR Board Game/Game Module")]
public class GameModule : ScriptableObject
{
    /// <summary>
    /// The room-wide identifier for this game. It is what travels in
    /// GameSelector.RequestGameServerRpc and what MenuControl stamps onto the generated key's
    /// keyInfo.keyName, so it is matched as a string in both directions — but only ever against
    /// this field, never against a literal in shared code.
    /// </summary>
    public string gameKey = "";

    /// <summary>
    /// The scene name exactly as it appears in EditorBuildSettings. A scene missing from that list
    /// fails in LoadScene with InvalidSceneName, visible only as a Debug.LogError from GameSelector.
    /// </summary>
    public string sceneName = "";

    /// <summary>Text on the menu key. Blank falls back to <see cref="gameKey"/>.</summary>
    public string menuLabel = "";

    /// <summary>
    /// The rules panel MenuControl builds when the menu opens. A direct reference, not a
    /// Resources.Load by name: the old lookup keyed off the active scene and **fell through to
    /// BASH's rules**, so a new game silently showed somebody else's.
    /// </summary>
    public TextAsset rulesText;

    /// <summary>
    /// Extra keys this game puts in the room menu, shown only while its scene is loaded. The
    /// behaviour lives in the game's own <see cref="IGameSession.InvokeMenuAction"/> — this is just
    /// the key. BASH's "Reset Game" and "Random Islands" were the reason MenuControl held a
    /// dictionary of scene names and direct references to two BASH classes.
    /// </summary>
    public GameMenuAction[] menuActions = new GameMenuAction[0];

    /// <summary>
    /// Leave the laser pointer switched on outside the menu, for a game whose pointer also touches
    /// the board. Applied by MenuControl on activeSceneChanged, so it is right from the first frame
    /// of the scene rather than from whenever a game component first gets an Update.
    /// </summary>
    public bool keepPointerAlwaysOn = false;

    public string MenuLabel => string.IsNullOrEmpty(menuLabel) ? gameKey : menuLabel;
}

/// <summary>
/// A menu key belonging to one game. Dispatched by <see cref="keyName"/>, like every other key in
/// this project — never by index.
/// </summary>
[System.Serializable]
public struct GameMenuAction
{
    public string keyName;
    public string label;

    public string Label => string.IsNullOrEmpty(label) ? keyName : label;
}
