/// <summary>
/// What shared code is allowed to know about a game.
///
/// Before this, MenuControl held direct type references to two BASH classes, GameSelector called
/// PlateauGame.Instance by name, and PlayerControls read Plateau's gemheart score onto the shared
/// avatar. Every one of those meant a fourth game had to be added to files the first three depend
/// on. Now the traffic goes the other way: a game registers itself and shared code asks the
/// interface.
///
/// Implement it on whatever object already owns the game's state — PlateauGame (which lives on the
/// persistent Room Anchor) or BashRoot (which lives in BashGame's scene) — and register in
/// Awake/OnNetworkSpawn, unregister in OnDestroy/OnNetworkDespawn. Returning null or doing nothing
/// is the normal answer for most of these; a game only implements what it actually has.
/// </summary>
public interface IGameSession
{
    /// <summary>
    /// The <see cref="GameModule.gameKey"/> this session belongs to. Naming your own game here is
    /// fine — it is shared code naming a game that is the problem.
    /// </summary>
    string GameKey { get; }

    /// <summary>
    /// The room is being sent into this game. Called **server-side and before the scene loads**, so
    /// the board almost certainly is not there yet — latch, do not act on scene contents.
    ///
    /// It has to be before the load rather than after because picking a game the room is already in
    /// is allowed to reset it, and GameSelector.LoadGameScene deliberately does nothing in that
    /// case, so a scene-load hook would never fire for Chasms -> Chasms.
    /// </summary>
    void OnGameSelected();

    /// <summary>
    /// One of this game's <see cref="GameModule.menuActions"/> was pressed. Dispatched by key name.
    /// Called on the pressing client only, so anything room-wide needs its own RPC.
    /// </summary>
    void InvokeMenuAction(string keyName);

    /// <summary>
    /// The label to float under a player's nametag, or null for none. Read every frame by
    /// PlayerControls for remote avatars, so keep it cheap and allocation-free where possible.
    /// </summary>
    string AvatarBadgeForSeat(int seat);
}
