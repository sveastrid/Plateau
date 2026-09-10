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

    // ------------------------------------------------------------------ the store

    /// <summary>
    /// What a player's saved library is keyed by. Stable forever and NEVER reused: the library
    /// outlives app updates, and on a real store it is also the local half of the Meta add-on
    /// identity. Convention here is "mrbg.&lt;game&gt;".
    ///
    /// It is deliberately not the catalog index. Reordering GameCatalog.games between two app
    /// versions would otherwise silently hand a player a different game than the one they bought.
    /// </summary>
    public string productId = "";

    /// <summary>
    /// What travels on the wire. 0..63, stable forever and NEVER reused: a room's combined library
    /// is a ulong mask, 8 bytes against a NetworkList of strings, and it rides in the connection
    /// approval payload before any object has spawned.
    ///
    /// -1 means "not a product" and is what an unfilled module reads as; GameCatalogValidator fails
    /// the build on it rather than letting it reach a headset.
    /// </summary>
    public int libraryBit = -1;

    /// <summary>The store's name for the game. <see cref="menuLabel"/> stays the short key label.</summary>
    public string displayName = "";

    [TextArea] public string blurb = "";

    public Sprite thumbnail;

    public bool isPaid = false;

    /// <summary>The Meta add-on SKU. Empty when free; required when <see cref="isPaid"/>.</summary>
    public string metaSku = "";

    /// <summary>
    /// MOCK ONLY. The real price is the formatted, localized string the platform returns —
    /// a hardcoded "$4.99" is wrong for most of the planet and is the sort of thing store review
    /// catches. MetaEntitlementService overwrites this and must never fall back to it.
    /// </summary>
    public string mockPriceLabel = "";

    /// <summary>
    /// Bounded by the Relay allocation and the ring, both 12 (RelayVivox.CreateRelay allocates for
    /// 12, PlayerRing has 12 slots).
    /// </summary>
    public int minPlayers = 2;
    public int maxPlayers = 12;

    public string MenuLabel => string.IsNullOrEmpty(menuLabel) ? gameKey : menuLabel;

    /// <summary>The store name, falling back to the menu label and then to the key.</summary>
    public string DisplayName => string.IsNullOrEmpty(displayName) ? MenuLabel : displayName;

    /// <summary>This game's bit in a library mask, or 0 when it has no valid bit.</summary>
    public ulong LibraryMask => libraryBit >= 0 && libraryBit < 64 ? 1UL << libraryBit : 0UL;
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
