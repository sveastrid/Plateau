# bigFixes1 — four bugs, and how to fix them

Working document for the four defects reported on 2026-09-04, in the style of the other docs in
`docs/`: a plan with the evidence attached, so a claim can be checked rather than taken on trust.

Nothing here has been applied. Every "verified" note below was checked against the working tree at
`1d265be`; everything marked "hypothesis" needs one measurement in the headset before it is worth
writing code for.

| # | Symptom | Root cause | Confidence |
| --- | --- | --- | --- |
| [1](#1--joining-a-room-hangs-on-joining-room) | Join hangs on "Joining room…", `NetworkConfig mismatch` | Four **Editor-only** Meta SDK prefabs are in `DefaultNetworkPrefabs.asset`; with `ForceSamePrefabs` the Editor and a device build can never agree | **Verified** |
| [2](#2--the-pointer-changes-did-not-reach-the-headset) | Pointer edits absent in the headset | Two separate things: a stale install, and a direction problem a position edit cannot fix. Plus a real beam-length bug | Mixed |
| [3](#3--the-bash-key-does-nothing-in-the-headset) | `BASH` key inert on device, fine in the Editor | Almost certainly the installed APK's scene list. The failure already logs — we just are not reading it | Hypothesis, cheap to settle |
| [4](#4--moving-a-bridge-a-second-time-highlights-the-same-plateaus) | Second bridge move highlights the first move's plateaus | Deliberate: the moved bridge is **lifted out of the network** before legality is computed. That is not the rule you want | **Verified**, and it is a rules decision |

---

## 0 — Before anything else: is the headset running *this* build?

Bugs 2 and 3 both have the shape "the Editor has it, the headset does not", which is what a stale
install looks like from the outside. This is worth ten seconds because it decides how much of §2 and
§3 is real work.

`BoardGames.apk` at the repo root is dated **2026-09-04 19:54**, and its IL2CPP metadata does
contain the BASH port (`BashGame`, `BashRoot`, `ControlListener`, `NetworkBaseControl`,
`PipeRenderer`, `Random Islands` are all string literals inside
`assets/bin/Data/Managed/Metadata/global-metadata.dat`). So the APK **on disk** is current. Whether
that APK is the one **installed on the headset** is the open question — `*.apk` is gitignored
(`.gitignore:69`) and there is no install step in this repo.

Check it, in this order:

1. In the headset, open the menu. If `Reset Game` / `Random Islands` are **absent everywhere**
   (including in BASH), the install predates commit `1d265be` outright.
2. Look at the pointer. If it is the short one coming out from under the hand, the install predates
   `4005993` — see §2.
3. Cheapest of all: rebuild and reinstall from the Editor, then re-run all three symptoms.

**If a fresh install fixes 2 and 3, the only real work in this document is §1 and §4.** Do that
install *first*; §1 is a genuine bug either way and will still be there afterwards.

> One caveat on rebuilding: after any package re-resolve or a cleared `Library/`, re-run
> `Tools/MetaSdkPatch/Apply-MetaSdkPatch.ps1` before building, per `CLAUDE.md`.

---

## 1 — Joining a room hangs on "Joining room…"

### What actually happens

Two independent defects stacked on top of each other. The first refuses the connection; the second
is why you never find out.

#### 1a. The connection is refused — and it is structural, not a stale build

`NetworkManager.NetworkConfig.ForceSamePrefabs` is `1`
(`Assets/Scenes/OpeningScene.unity:439`). With it on, Netcode folds **every registered network
prefab's `GlobalObjectIdHash`** into the config hash that a joining client sends and the server
compares — the sorted `Prefabs.NetworkPrefabOverrideLinks` keys, in
`Library/PackageCache/com.unity.netcode.gameobjects@cfd429cc91ec/Runtime/Configuration/NetworkConfig.cs`,
`GetConfig()`. Any difference in the *set* of registered prefabs is a hard refusal.

`Assets/DefaultNetworkPrefabs.asset` — the single list the NetworkManager points at
(`OpeningScene.unity:429-430`, guid `281c798d4b49ebf4ea07c1feb886611e`) — holds **twelve** entries.
Only four are this project's:

| Entry | Where it lives |
| --- | --- |
| `Player.prefab` | `Assets/Prefabs/` |
| `Room Anchor.prefab` | `Assets/Prefabs/` |
| `Base.prefab` | `Assets/Prefabs/Bash/` |
| `NetworkCannonLine.prefab` | `Assets/Prefabs/Bash/` |

The other eight are Meta XR Core SDK **Building Blocks** prefabs living in
`Library/PackageCache/com.meta.xr.sdk.core@4ea2676097b9/`. Netcode's own asset post-processor put
them there — see 1b. **Four of those eight are inside the package's `Editor/` folder:**

| Prefab | Path inside the package | `GlobalObjectIdHash` |
| --- | --- | --- |
| `NetworkedAvatarNGO.prefab` | `Editor/BuildingBlocks/BlockData/MultiplayerBlocks/NGO/NetworkedAvatar/Prefabs/` | 81852899 |
| `NetworkedAvatarNGO28Plus.prefab` | `Editor/BuildingBlocks/BlockData/MultiplayerBlocks/NGO/NetworkedAvatar/Prefabs/` | 3033352061 |
| `NetworkedCharacterSpawnerNGO.prefab` | `Editor/BuildingBlocks/BlockData/MultiplayerBlocks/NGO/NetworkedCharacterRetargeter/Prefabs/` | 1320522728 |
| `PlayerNameTagSpawnerNGO.prefab` | `Editor/BuildingBlocks/BlockData/MultiplayerBlocks/NGO/PlayerNameTag/Prefabs/` | 3427852951 |

An asset under a folder named `Editor` is Editor-only and **is not included in a player build**. So:

- in the **Editor**, the list resolves all twelve and registers twelve hashes;
- in the **Quest build**, those four references are null, `NetworkPrefab.Validate()` drops them, and
  eight hashes are registered.

Twelve ≠ eight, so the config hashes differ **every time**, and they will differ no matter how many
times you rebuild. An Editor↔Editor pair matches. A device↔device pair matches. **Editor↔device can
never match**, which is exactly the case you hit.

The warning is logged by whichever side is the *server*
(`.../Runtime/Messaging/Messages/ConnectionRequestMessage.cs:142-151` and `:169-178`), and it calls
`DisconnectClient(senderId)` with **no reason string**.

#### 1b. It will come back if you only delete the entries

`.../com.unity.netcode.gameobjects@.../Editor/Configuration/NetworkPrefabProcessor.cs` is an
`AssetPostprocessor`. It re-adds any imported prefab carrying a `NetworkObject` to the list
(`ProcessImportedAssets`, line 39), and when the list asset does not exist it creates it and fills
it from `AssetDatabase.FindAssets("t:GameObject")` — **which searches packages too** (`FindAll()`,
line 170). That is how the Meta prefabs got in.

The whole postprocessor is gated on one flag (line 33-37):

```csharp
var settings = NetcodeForGameObjectsProjectSettings.instance;
if (!settings.GenerateDefaultNetworkPrefabs) { return; }
```

`NetcodeForGameObjectsProjectSettings` is a `ScriptableSingleton` at
`ProjectSettings/NetcodeForGameObjects.asset`, with `GenerateDefaultNetworkPrefabs = true` by
default. **That file does not exist in this project**, so the default is in force. Every package
re-resolve — which `CLAUDE.md` already tells us happens often enough to need a patch script re-run —
re-imports the Meta prefabs and re-adds them.

#### 1c. The lobby never notices

`GameController.TryToJoinRelayVivox()` (`Assets/Scripts/GameController.cs:184-210`) awaits
`JoinRelay`, catches only `RelayServiceException`, then sets `"Joining room..."` and returns. Relay
succeeded — the *room code* was fine; it was Netcode that refused a moment later. Nothing in the
project subscribes to `NetworkManager.OnClientDisconnectCallback` on the joining side: the only
subscription is `RoomAnchor.cs:74`, and that is the server clearing the world-grab lock. So the
client sits on "Joining room…" until the app is restarted.

### The fix

**Step 1 — prune the list.** Open `Assets/DefaultNetworkPrefabs.asset` in the Inspector and remove
the eight Meta entries, leaving `Player`, `Room Anchor`, `Base`, `NetworkCannonLine`. (The asset is
plain YAML and can be hand-edited; each entry is six lines. The four project GUIDs to keep are
`9243d030…`, `d94b95cb…`, `df7f4d0b…`, `1ccd5357…`.)

**Step 2 — stop it regenerating.** *Edit ▸ Project Settings ▸ Netcode for GameObjects* → uncheck
**Generate Default Network Prefabs List**. That writes `ProjectSettings/NetcodeForGameObjects.asset`.
`ProjectSettings/` is not gitignored, so **commit that file** — otherwise the next person to clone
is back where we started. From then on the list is hand-maintained, which is the right trade here:
this project has four network prefabs and adds one about twice a year, and the current arrangement
silently breaks every cross-platform session.

**Step 3 — make a refused join visible.** In `GameController`, subscribe to the disconnect callback
and reuse the existing "wrong room code" recovery path. Sketch:

```csharp
// Start(), not OnEnable: NetworkManager.Awake must have run for Singleton to exist, and both live
// in OpeningScene with no ordering guarantee between them.
void Start()
{
    ...
    if (NetworkManager.Singleton != null)
    {
        NetworkManager.Singleton.OnClientDisconnectCallback += HandleClientDisconnect;
    }
}

void OnDestroy()
{
    if (NetworkManager.Singleton != null)
    {
        NetworkManager.Singleton.OnClientDisconnectCallback -= HandleClientDisconnect;
    }
}

/// <summary>
/// A join that Relay accepted but Netcode refused. The server logs the reason and disconnects us
/// with no reason string (ConnectionRequestMessage.Deserialize), so all we get is this callback —
/// but "the host refused" beats sitting on "Joining room..." forever.
/// </summary>
void HandleClientDisconnect(ulong clientId)
{
    NetworkManager nm = NetworkManager.Singleton;
    if (nm == null || nm.IsServer)
    {
        return;                       // the host seeing somebody else leave
    }

    string why = string.IsNullOrEmpty(nm.DisconnectReason)
                     ? "The room refused the connection (build mismatch?)."
                     : nm.DisconnectReason;
    ShowJoinFailed(why);
}
```

`ShowJoinFailed` should be the body of the existing `catch (RelayServiceException)` block
(`GameController.cs:190-202`) lifted into a method, so the lobby returns to the room-code prompt and
`Enter` works again. Do that lift regardless — the two paths have to agree about what "back to the
keyboard" means.

Add a **watchdog** as well. A transport-level failure (DTLS, Relay allocation gone stale) may never
raise the disconnect callback at all. A coroutine started at `StartClient()` that calls
`ShowJoinFailed("Could not reach the room.")` if neither `IsConnectedClient` nor a disconnect has
happened within ~15 s covers the rest of the failure space for about ten lines.

**Step 4 (optional, recommended) — a guard rail so this cannot silently return.** A build
preprocessor that fails the build rather than shipping a divergent list:

```csharp
// Assets/Editor/NetworkPrefabListCheck.cs
public class NetworkPrefabListCheck : IPreprocessBuildWithReport
{
    public int callbackOrder => 0;

    public void OnPreprocessBuild(BuildReport report)
    {
        var list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>("Assets/DefaultNetworkPrefabs.asset");
        foreach (var entry in list.PrefabList)
        {
            string path = AssetDatabase.GetAssetPath(entry.Prefab);
            // Anything outside Assets/, or under any Editor/ folder, is present in the Editor and
            // absent in the player. With ForceSamePrefabs that is a guaranteed NetworkConfig
            // mismatch between an Editor host and a device client.
            if (!path.StartsWith("Assets/") || path.Contains("/Editor/"))
            {
                throw new BuildFailedException(
                    "DefaultNetworkPrefabs.asset contains an Editor-only or package prefab: " + path);
            }
        }
    }
}
```

### Verification

Editor hosts, headset joins (and then the other way round). Neither console shows
`NetworkConfig mismatch`, and the joiner lands in the host's current scene. If it still mismatches,
log `NetworkManager.Singleton.NetworkConfig.Prefabs.NetworkPrefabOverrideLinks.Count` on both sides
at `OnClientStarted` / `OnServerStarted` — the two numbers must be equal, and that reduces the whole
problem to one integer.

---

## 2 — The pointer changes did not reach the headset

### What is actually on disk

Both of your edits are committed and present, on the **`PersistentRig.prefab` instance override**,
not on `Pointer.prefab` itself:

| | `Pointer.prefab` (the asset) | `PersistentRig.prefab` instance override (`:12203-12297`) — **this is what runs** |
| --- | --- | --- |
| Local position | `(0, -0.100, 1)` | `(0, -0.011, 2.006)` |
| Local scale | `(0.01, 1, 0.01)` | `(0.005, 2, 0.005)` |

So the asset holds the old "1 m, hanging under the hand" values, the instance holds the new ones, and
the instance wins. **Nothing is wrong with the data.** Which leaves three candidate explanations,
and they are not exclusive:

### Cause A — the headset is on an older install

`PersistentRig.prefab` was created in commit `4005993` (2026-09-04 17:19) and has not been touched
since. Settle it with §0 before doing anything else.

### Cause B — position cannot fix a direction problem (verified as a structural difference)

`Right Hand` on `PersistentRig` carries the legacy `TrackedPoseDriver` with
`m_Device: 1` (GenericXRController) and `m_PoseSource: 5` (RightPose)
(`Assets/Prefabs/PersistentRig.prefab:3977-3978`). That is the controller's **grip** pose. Its `+Z`
runs along the handle, which is pitched steeply down from where a person thinks they are aiming.
The Pointer is rotated exactly 90° about X, so its beam axis *is* `Right Hand`'s `+Z` — the grip
axis. Hence "it comes out from underneath the hand".

In the Editor there is no controller, so `Right Hand` keeps its authored identity rotation
(`PersistentRig.prefab:3953`) and the beam runs dead ahead and level. **The Editor and the headset
genuinely differ here, and translating the Pointer up in local Y changes nothing about the
direction** — which is consistent with "I moved it up and it did not help".

The project cannot switch to the OpenXR *aim* pose: that needs the new Input System's
`TrackedPoseDriver`, and `CLAUDE.md` §"Opening the project" pins Active Input Handling to
**Input Manager (Old)**. So apply a pitch offset instead, and apply it **only when a controller is
actually present** so the Editor keyboard path is not made worse. `InputReader` already publishes
`RightControllerFound` (`InputReader.cs:16`):

```csharp
// On the Pointer. Order 23: before PointerBeam (24), which raycasts from this transform.
[DefaultExecutionOrder(23)]
public class PointerAim : MonoBehaviour
{
    [Tooltip("Degrees to pitch the beam UP from the controller's grip axis. Tune in the headset; " +
             "the grip pose points along the handle, not along where the player thinks they aim.")]
    public float AimPitchDegrees = 45f;

    InputReader inputs;

    void Update()
    {
        if (inputs == null)
        {
            GameObject go = GameObject.Find("Input Reader");
            inputs = go != null ? go.GetComponent<InputReader>() : null;
        }

        // No controller means the Editor keyboard fallback, where Right Hand keeps its authored
        // identity rotation and the beam already points where it looks like it should.
        float pitch = (inputs != null && inputs.RightControllerFound) ? AimPitchDegrees : 0f;
        transform.localRotation = Quaternion.Euler(90f - pitch, 0f, 0f);
    }
}
```

45° is a starting guess, not a measurement — expect to land somewhere in 40–60°. It is a serialized
field precisely so it can be tuned live.

### Cause C — a real bug: the beam is drawn twice as long as it is measured

`PointerBeam.DrawBeam()` (`Assets/Scripts/Plateau/PointerBeam.cs:98-122`) computes `length` in
**metres** — `DefaultLength`, or `Hit.distance` — and then writes it straight into the child's local
scale:

```csharp
float half = length * 0.5f;
visibleBeam.localScale = new Vector3(1f, half, 1f);
visibleBeam.localPosition = new Vector3(0f, -1f + half, 0f);
```

Those are units of the **Pointer root's** local space, and the Pointer root is now scaled `y = 2`.
So every drawn beam is **twice** its measured length: 4 m when it is pointing at nothing, and — worse
— 2× the hit distance when it is pointing at something, so the visible beam shoots straight through
whatever you are aiming at. The raycast itself is correct (`Origin` uses `TransformPoint`, which
accounts for scale), so this is purely the drawing. It was invisible before because the root used to
be at `y = 1`.

Fix, in `DrawBeam()`:

```csharp
// localScale.y is a unit conversion, not decoration: this object is scaled on Y to set the
// pointer's reach, so `length` (metres) must be divided by it before it goes into a LOCAL scale.
// The near end is at local y = -1 whatever the scale, which is why Origin above needs no change.
float unit = Mathf.Max(1e-4f, Mathf.Abs(transform.localScale.y));
float half = (length / unit) * 0.5f;
```

While you are here: `pointerControl`'s trigger capsule is the Pointer root's own `CapsuleCollider`
(height 2, direction Y), so its reach is governed by the same `localScale.y`. Right now the pointer
physically reaches **4 m**, spanning `z = 0.006 … 4.006` in hand-local space. If you want a true 2 m
pointer, set the `PersistentRig` override to `localScale = (0.005, 1, 0.005)` and
`localPosition = (0, -0.011, 1)` — the capsule then spans `0 … 2` — and leave `DefaultLength = 2`.
Do that *and* the `DrawBeam` fix, or you will have moved the discrepancy rather than removed it.

---

## 3 — The `BASH` key does nothing in the headset

### What is definitely fine

Everything from the key to the scene list, in the current tree:

- `Menu1.prefab` and `Menu2.prefab` both carry a key with `keyInfo.keyName: BASH` — so it does not
  matter whether the headset is the host or a client.
- That key's GameObject is tagged `key`, and has a `BoxCollider` **and** a `Rigidbody`, which
  `pointerControl`'s trigger callbacks require (`Menu1.prefab:345-365`).
- `MenuControl.HandleKey` has the case (`MenuControl.cs:202-207`), and `KeyScene`
  (`MenuControl.cs:52-56`) scopes only `Reset Game` and `Random Islands` — `BASH` is never filtered
  out.
- `GameRoutes.SceneByKey` maps `"BASH" → "BashGame"` (`GameRoutes.cs:35`).
- `Assets/Scenes/BashGame.unity` is in the build list, enabled, at index 4 — **added in commit
  `1d265be`**, the BASH port itself (`ProjectSettings/EditorBuildSettings.asset`).

That last line is the interesting one: `BashGame` did not exist in the scene list before
2026-09-04 17:42. **Any APK built before that moment has the BASH key, the BASH code, and no BASH
scene**, which produces exactly "the key presses, nothing happens".

### Read the answer out of the headset

You do not have to guess — the failure already logs, and `DebugLog` mirrors
`Application.logMessageReceived` into the in-headset box (`CLAUDE.md` §"Debugging in the headset").
Press `BASH` in the headset and read the box. There are only four ways this dies, and each says so:

| What the box says | What it means |
| --- | --- |
| `GameSelector: LoadScene("BashGame") returned InvalidSceneName.` (`GameSelector.cs:90`) | **The installed build's scene list has no BashGame.** Rebuild and reinstall. This is the expected answer. |
| `MenuControl: no local player yet, cannot switch to 'BASH'.` (`MenuControl.cs:279`) | The local `PlayerObject` had not spawned. Transient; press it again. |
| `GameSelector: ignoring 'BashGame', a scene load is already running.` (`GameSelector.cs:68`) | `s_SwitchInProgress` is stuck — a previous load never completed. It is `static`, so with domain reload off it also survives a Play-mode restart in the Editor. |
| **Nothing at all** | The key press never reached `HandleKey`. Check the pointer is on the key (see §2 — the grip-pose tilt makes aiming in the headset genuinely harder than in the Editor). |

### The fix

Whichever line appears, the repair is small:

- **`InvalidSceneName`** → rebuild and reinstall (§0). No code change.
- **`s_SwitchInProgress` stuck** → it is only cleared by `HandleLoadEventCompleted`, which is
  subscribed *inside* `LoadGameScene` and unsubscribed there. Add a timestamp beside it and treat
  the flag as expired after `NetworkConfig.LoadSceneTimeOut`, so a lost load event cannot wedge
  game switching for the life of the session.

Independently of which one it is, **make the failure impossible to miss at room creation** rather
than at the moment somebody presses the key. `GameRoutes` knows every scene the room can reach, so
validate it once when the host opens the room:

```csharp
// GameSelector, called from GameController.HostNewRoom after StartHost().
/// <summary>
/// Every scene in GameRoutes must be in the build's scene list. LoadScene fails with
/// InvalidSceneName otherwise, and the only trace is one LogError at the moment a player presses
/// the key — long after the build that caused it. Say it at room creation instead.
/// </summary>
public static void WarnAboutMissingScenes()
{
    foreach (string scene in GameRoutes.AllScenes)          // needs a small accessor on GameRoutes
    {
        if (SceneUtility.GetBuildIndexByScenePath("Assets/Scenes/" + scene + ".unity") < 0)
        {
            Debug.LogError("GameRoutes lists '" + scene + "', but this build has no such scene. " +
                           "Add it to File > Build Settings and rebuild.");
        }
    }
}
```

Four lines of production value: it turns a silent dead key into a named error the first time the
build is run, in the headset, where you will see it.

---

## 4 — Moving a bridge a second time highlights the same plateaus

> **Superseded and applied — see [`bridgeMovementUpdate.md`](bridgeMovementUpdate.md).** The three
> ranked options below are closed, not open. The third (seed the search from the moved bridge's own
> endpoints) was the one chosen and shipped; this section ranked it last and argued against it
> because it was reasoning about which bridges count, when the actual disagreement was about where
> the search starts. Read the rest of this section as history.

### This one is not a mistake — it is a reading of the rule, and it is the wrong one

`plateauRules.md:61`: *"It must be repositioned to span from a plateau already connected by that
player's bridges to a new plateau."*

The code reads "already connected" as **after the bridge you are moving has been lifted off the
board**. `PlateauSelection.RecomputeLegal` (`PlateauSelection.cs:565`):

```csharp
// Relocating a bridge computes the network as if that bridge were already lifted, so one at
// the end of a chain is not propping up its own legality.
int exclude = selected.IsPlacedBridge ? selected.edge : -1;
if (!game.TryBuildView(seat, exclude, out PlateauMoveRules.View view))
```

and `PlateauGame.TryBuildView` (`PlateauGame.cs:809-821`) then skips that edge when it fills both
`ownBridge` and `anyBridge`. `PlateauMoveRules.ConnectedComponent` (`PlateauMoveRules.cs:250-283`)
walks only `ownBridge` edges out from the central plateau, so the excluded bridge contributes
nothing to the component.

Play that through with the two bridges everyone starts with:

| | Network before | Component used | Highlighted |
| --- | --- | --- | --- |
| Place bridge A, central → P1 | `{central}` | `{central}` | the 6 plateaus around the centre |
| Place bridge B, P1 → P2 | `{central, P1}` | `{central, P1}` | centre's 5 free neighbours + P1's neighbours |
| **Move bridge A** | `{central, P1, P2}` | **`{central}`** — A lifted, so P1 and P2 fall out with it | **the same 6 as move one** |

That last row is the bug you are seeing, and the collapse is total: picking up the bridge nearest the
centre throws away every bridge hanging off it. It is worse the more bridges are on the board.

Your statement of the rule — *"it should let me connect the bridge to any plateau that is currently
connected by bridges to an adjacent one"* — is the other reading: the network is evaluated **as it
currently stands**, including the bridge in your hand. That is the one to implement.

### The fix

The moved bridge needs to stay in `ownBridge` (so its reach still counts toward the component) and
stay in `anyBridge` (so the gap it currently sits in is not offered back to you as a destination).
Both of those are what happens when nothing is excluded at all — so the fix is to **stop passing an
exclusion**, in two places that must change together:

```csharp
// PlateauSelection.RecomputeLegal — was: selected.IsPlacedBridge ? selected.edge : -1
//
// The network is read as it currently stands, bridge-in-hand included: plateauRules.md's "a
// plateau already connected by that player's bridges" is about the board in front of you, not
// about the board with your own bridge lifted off it. Its current gap stays flagged in
// anyBridge, so "move it to where it already is" is still not offered.
if (!game.TryBuildView(seat, -1, out PlateauMoveRules.View view))
```

```csharp
// PlateauGame.RequestMoveBridgeServerRpc — was: TryBuildView(seat, fromEdge, out view)
// The server must run the SAME view the client highlighted from, or it will refuse exactly the
// destinations the client just lit up. This pairing is the whole point of PlateauMoveRules.
if (!TryBuildView(seat, -1, out PlateauMoveRules.View view))
```

Nothing else moves. `BridgeDestinations` (`PlateauMoveRules.cs:184-220`) already skips
`anyBridge[e]` edges and already requires exactly one end inside the component, and
`TryResolveBridgeEdge` (`:296-338`) picks the gap from the same view — so both keep working, and the
`excludeEdge` parameter on `TryBuildView` becomes dead and can be deleted along with its callers'
argument.

Re-run the table above and the third row becomes: component `{central, P1, P2}` → highlighted =
every free gap with exactly one end in `{central, P1, P2}`. That is what you asked for.

### The consequence to decide on before we ship it

Under this reading a bridge can move to a gap that only *its own presence* makes reachable, and so
can strand the rest of the network. Concretely: with A on `central–P1` and B on `P1–P2`, you may
move A to `P2–P3`, and now nothing connects `central` to `P1` at all. B and A are floating, and
`ConnectedComponent` — which is seeded from the central plateau — will never see them again. The
player's bridge network is permanently orphaned and they cannot lay another bridge out there.

The current "lift it first" rule exists to prevent exactly that. Three ways to go, in the order I
would rank them:

1. **Accept it.** It is a legal-but-bad move, like walking a piece into a corner, and the rules do
   not forbid it. Simplest, and the game is not turn-enforced yet anyway.
2. **Guard the outcome, not the input** — the version I would build. Keep the change above, and add
   one check server-side and one client-side: after applying the move hypothetically, is every one
   of this player's bridges still in the closure of the central plateau? If not, the destination is
   not offered and the RPC is refused. That is one extra `ConnectedComponent` call over an already
   4-element array, and it gives you the frontier you want *and* keeps the network a tree rooted at
   the centre. It belongs in `PlateauMoveRules` beside the rest of the rule, so client and server
   cannot disagree about it.
3. Leave the lift in and instead **seed the component with the moved bridge's own endpoints**. Cheap,
   but it is a third reading of the rule that matches neither the doc nor your description, and it
   would need its own comment defending it. I would not.

### Two smaller things noticed in the same code, worth fixing while it is open

- `PlateauSelection.Send` (`:503-511`) passes `selected.edge` into
  `RequestMoveBridgeServerRpc(byte fromEdge, …)`. Edge indices are bytes throughout
  (`PlacedBridge.edge`, `PlateauPieceTag.edge`) and `PlateauConst.NoIndex` is **255**, so the board
  is capped at 255 edges and index 255 is unusable. There are 81 bridge spots today, so this is not
  live — but it is one authored row away from being live, and it will fail as a bridge that silently
  refuses to move. Worth an `Assert` in `PlateauBoard`'s bake, not a redesign.
- `RecomputeLegal` is called from `Select()` and whenever `BoardChanged` fires, which is correct —
  but `BridgeDestinations` and `TryResolveBridgeEdge` each recompute `ConnectedComponent` from
  scratch into the same `s_component` static. Harmless today (single-threaded, sequential calls);
  worth a note if anything ever calls them interleaved.

---

## 5 — Suggested order of work

1. **§0**: rebuild and reinstall on the headset. Re-test bugs 2 and 3. Ten minutes, and it decides
   how much of §2 and §3 is real.
2. **§1 steps 1–2**: prune `DefaultNetworkPrefabs.asset` and turn off the auto-generator. This is
   the one bug that blocks all multi-device testing, so nothing else can be properly verified until
   it is done.
3. **§1 step 3**: the disconnect handler. Small, and it means the next networking failure reports
   itself instead of hanging.
4. **§4**: the bridge rule — answer the question in §4's "consequence" section first.
5. **§2 causes B and C**: the aim-pitch offset and the `DrawBeam` scale fix. Both need headset
   iteration, so batch them with a build.
6. **§1 step 4 and §3's `WarnAboutMissingScenes`**: the two guard rails, once everything works.

---

## 6 — Questions

1. **Bug 1** — when the mismatch warning appeared, which side was hosting? The message is logged by
   the *server* (`ConnectionRequestMessage.cs:146`), so if you saw it in the Editor console then the
   Editor was the host and the headset was joining. Knowing which confirms the direction, though the
   diagnosis in §1a holds either way.
2. **Bugs 2 and 3** — when was the headset APK last built and installed, relative to yesterday
   evening's two commits (`4005993` at 17:19, `1d265be` at 17:42)? This is the single measurement
   that settles both.
3. **Bug 3** — what does the in-headset debug box say when you press `BASH`? And does `Chasms` work
   on the same headset? If Chasms switches and BASH does not, that is `InvalidSceneName` and nothing
   else.
4. **Bug 4** — the orphaning question in §4: option 1 (allow it) or option 2 (refuse a move that
   would strand your own bridges)? I would build option 2, but it is your rule to make.
5. **Bug 2** — when you say the pointer should come "out of the front of the right hand", do you
   mean parallel to where the controller is aimed (the OpenXR *aim* pose, i.e. the pitch offset in
   §2's Cause B), or simply raised so it emerges from the top of the controller rather than the
   bottom? They are different fixes and only the first will look right when you tilt your wrist.
