# updates1.md

Shared player avatars, `StairsGame` as the entry scene, and room-wide game switching.

**Audience:** the developer doing the work. Every claim below references a real file, a real
line, or a real serialized value in this repo as of 2026-07-29, branch `mr-passthrough`.
Read `MRTemplate.md` first if you have not — §1.5 of that document lists MR settings that must
not be undone, and nothing here overrides them.

---

## 0. What is being asked for

| # | Requirement | One-line answer |
|---|---|---|
| 1 | When a user joins a room, their tornado body, face, hands and username are visible to everyone else | The prefab and the sync code already exist. They break because the avatar binds to scene objects once and never rebinds — §2.2 |
| 2 | The first scene entered after the lobby is `StairsGame` | Create the scene, and have the **host** load it through `NetworkManager.SceneManager` instead of `UnityEngine.SceneManager` — §2.1 |
| 3 | Any player picking a different game moves everyone to that scene immediately (`Chasms` → `ChasmGame`) | A `ServerRpc` from the pressing client → the server calls `NetworkManager.SceneManager.LoadScene(..., Single)` → Netcode moves every client, and carries the spawned players across — §2.3 |

These three are one job, not three. Requirement 3 is what makes requirement 1 hard: an avatar
that survives *one* scene load is a different problem from an avatar that survives *every* game
switch for the rest of the session.

Estimated effort: two new scripts (~130 lines total), edits to four existing scripts, two new
scenes duplicated from `GameScene`, and four components deleted. Half a day plus device testing.

---

## 1. Where the code stands today

### 1.1 Session flow

Two scenes in `ProjectSettings/EditorBuildSettings.asset`: `OpeningScene` (index 0, the lobby)
and `GameScene` (index 1, the room). `GameScene` is the MR-configured room shell — no skybox
(`m_SkyboxMaterial: {fileID: 0}`), `Main Camera` on Solid Color with `a: 0`, an empty `World Root`
for game content, and a fully wired `Menu Manager`. It is the right thing to duplicate.

In the lobby, `GameController.Update()` collects a room code and then a username off the laser
keyboard. On the final Enter (`GameController.cs:130-144`):

- **empty room code** → `relayVivoxStarter.StartRelayAndVivox(nickName)` then
  `SceneManager.LoadScene("GameScene")` (`GameController.cs:136`)
- **non-empty code** → `TryToJoinRelayVivox()` → `await JoinRelayAndVivox(...)` then
  `SceneManager.LoadScene("GameScene")` (`GameController.cs:168`)

`RelayVivox` (`Assets/Scripts/RelayVivox.cs`) allocates or joins a Unity Relay slot over DTLS and
calls `StartHost()` / `StartClient()`. It sits on the `Network Manager` GameObject in
`OpeningScene` alongside `NetworkManager` and `UnityTransport`.

> **Correction to `MRTemplate.md` §1.1.** That document says `Network Manager` survives the scene
> load "via `PersistentObject` (`DontDestroyOnLoad`)". It does not — `PersistentObject.cs` is
> attached to **nothing** in this project, and neither is `NetworkReconnectHandler.cs`. The object
> survives because Netcode's own `NetworkManager.OnEnable()` calls `DontDestroyOnLoad(gameObject)`
> (`Library/PackageCache/com.unity.netcode.gameobjects@0f12e689d980/Runtime/Core/NetworkManager.cs:1100`).
> This still works, but do not "fix" it by deleting the NetworkManager or reparenting it —
> `NetworkManagerCheckForParent()` at line 1098 means a parented NetworkManager is **not**
> made persistent.

### 1.2 The avatar

`NetworkConfig.PlayerPrefab` is `Assets/Prefabs/Player.prefab`
(`OpeningScene.unity:499`, guid `9243d030bab0c254c9a752ec296af249`), and it is registered in
`Assets/DefaultNetworkPrefabs.asset`. Netcode spawns one per client automatically on connect.

Its hierarchy — **child order is load-bearing**, because `PlayerControls` resolves by index:

| Index | Object | What it is | Read by |
|---|---|---|---|
| 0 | `tornado` | `Assets/tornado.fbx` — the body | nothing; rides the root transform |
| 1 | `Username` | `TextMeshPro`, LiberationSans SDF | `PlayerControls.cs:37, 68` |
| 2 | `PlayerLeft` | `OculusHandPinchArrowBlended.fbx` | `PlayerControls.cs:40` |
| 3 | `PlayerRight` | same mesh | `PlayerControls.cs:42` |
| 4 | `mainFace` | wraps `Assets/face.obj` | `PlayerControls.cs:43` |

Replication (`Assets/Scripts/PlayerControls.cs:20-27`):

- root position — `ClientNetworkTransform` (owner-authoritative, `SyncPosition*` only)
- `facePos` / `faceRot`, `lHPos` / `lHRot`, `rHPos` / `rHRot` — owner-write `NetworkVariable<Vector3>`
- `playerName` — server-write `NetworkVariable<FixedString32Bytes>`, set by `SetPlayerNameServerRpc`
- the owner disables all five children locally (`PlayerControls.cs:71-77`) so you don't see your
  own floating head

So the mechanism for requirement 1 is already built. It is the **binding** that fails.

### 1.3 The menu

`Assets/Prefabs/Menu1.prefab` is the "Choose a Game" menu, with two `keyInfo` keys whose
`keyName` values are already correct: `Stairs` and `Chasms`. `MenuControl` (rewritten since
`MRTemplate.md` was written — it now dispatches by name, not child index) opens it on **X**,
reads `pointerControl.currentLetter` on trigger, and dispatches in `HandleKey`
(`MenuControl.cs:77-99`). Both game keys currently fall through to:

```csharp
default:
    Debug.Log("MenuControl: '" + keyName + "' pressed — no game is wired to that key yet.");
```

`Menu Manager` in `GameScene` is fully wired (`inputs`, `Menu1`, `myCam`, `pointer`, `worldRoot`,
`passthrough`). Duplicating the scene preserves all of it.

---

## 2. Four root causes

Fix all four. Fixing any three leaves a symptom that looks like one of the others.

### 2.1 The lobby loads the room scene behind Netcode's back

`NetworkConfig.EnableSceneManagement` is `1` (`OpeningScene.unity:511`). That means **the server
owns scene loading**: it tells clients what to load, it synchronizes a joining client into the
scenes it already has open, and it carries spawned `NetworkObject`s across the transition.

`GameController.cs:136` and `:168` call `UnityEngine.SceneManagement.SceneManager.LoadScene`
instead. Netcode is never told. The two paths are also asymmetric:

- **Host:** `StartRelayAndVivox` is `async void` and is *not* awaited, so `LoadScene("GameScene")`
  runs on the very next line — before the Relay allocation resolves and before `StartHost()`.
  The host therefore ends up in `GameScene` *before* the session starts. Accidentally fine.
- **Client:** `TryToJoinRelayVivox` **awaits** `JoinRelayAndVivox`, which calls `StartClient()`.
  So the client connects **while still in `OpeningScene`**, and only then loads `GameScene`
  locally — while Netcode is simultaneously trying to synchronize it into the server's scene.

That asymmetry is why the symptom is intermittent. It also makes requirement 3 impossible as
written: there is no mechanism by which one client's key press can move anyone else.

### 2.2 `PlayerControls` binds to scene objects once, at spawn, and never rebinds

`PlayerControls.OnNetworkSpawn()` (`PlayerControls.cs:29-83`) caches four references that live in
the **current scene**:

```csharp
myCam      = Camera.main != null ? Camera.main.transform : null;   // line 38
localLeft  = FindTransform("Left Hand");                           // line 39
localRight = FindTransform("Right Hand");                          // line 41
rig        = FindTransform("XRRig");                               // line 44
```

and calls `Setup(this)` on the scene's `CameraController2` and `MenuControl` (lines 54-65).

The `Player` object is a dynamically spawned `NetworkObject`. Netcode keeps it alive across a
scene load. The rig, the camera, the hands and the `Menu Manager` are **destroyed and replaced**.
Every one of those references is dangling the instant the room changes scene.

Two concrete failures fall out of this:

1. **Today, on the joining client.** Because of §2.1 the client connects in `OpeningScene`, so
   `OnNetworkSpawn` binds `myCam` to the *lobby's* `Main Camera`. The local `LoadScene` then
   destroys it. From that point `PlayerControls.Update()` hits
   `if (myCam == null) { return; }` at line 90 on **every** frame for **every** remote avatar
   this client is watching. Hands, face and the username billboard never update again — they sit
   at the origin — while the tornado body still tracks, because the root is driven by
   `ClientNetworkTransform`, not by `Update()`. A half-drawn avatar is exactly the symptom
   requirement 1 describes.
2. **After the fix, on every game switch.** `Stairs → Chasms` destroys the rig and the menu.
   Without a rebind, `MenuControl.myPlayer` is `null` in the new scene, so nobody can pick a
   third game — the room is stuck in whatever it switched to.

### 2.3 Nothing carries a game choice to the other players

`MenuControl.HandleKey` logs and returns. There is no `NetworkBehaviour` in the project that a
non-host client could use to ask the server for anything except `PlayerControls`' two name/owner
RPCs. This needs building — §3 Step 5.

### 2.4 Four orphan in-scene NetworkObjects

| Scene | GameObject | Why it is there | Why it is a problem |
|---|---|---|---|
| `OpeningScene` | `Game Manager` | `GameController : NetworkBehaviour` (`GameController.cs:17`) — but it declares no `NetworkVariable` and no RPC | Forces a `NetworkObject` onto a lobby-only object |
| `OpeningScene` | `XRRig` | leftover | Nothing networked on it — no `NetworkBehaviour` |
| `OpeningScene` | `Left Hand` | leftover | same |
| `OpeningScene` | `Right Hand` | leftover | same |
| `GameScene` | `XRRig` | leftover | Components are `XROrigin`, `NetworkObject`, `CameraController2`, `OVRManager`, `OVRPassthroughLayer`, `PassthroughController`. Not one of them is a `NetworkBehaviour`. |

An in-scene placed `NetworkObject` is spawned by the server and matched on clients by
`GlobalObjectIdHash`. The rig and the hands are **per-client local hardware** — there is one per
player and they are not meant to be shared objects at all.

Right now this is mostly inert, because the host reaches `GameScene` before `StartHost()` runs
(§2.1). **The fix in §3 Step 7 changes that**: the host will call `StartHost()` while still in
`OpeningScene`, which means the lobby's four in-scene `NetworkObject`s get spawned and then
immediately despawned by the scene change, and a client joining later has four in-scene
`NetworkObject`s in its lobby with no server counterpart. Clean them up **before** changing the
flow, not after, or you will spend an afternoon reading Netcode synchronization warnings that
have nothing to do with your actual change.

---

## 3. The work, in order

Each step leaves the project compiling. Do them in this order.

### Step 1 — Delete the orphan `NetworkObject`s *(required, do first)*

1. Change `GameController` to a plain `MonoBehaviour`:

   ```csharp
   // Assets/Scripts/GameController.cs:17
   -public class GameController : NetworkBehaviour
   +public class GameController : MonoBehaviour
   ```

   It uses nothing from `NetworkBehaviour`. Leave the `using Unity.Netcode;` — Step 7 adds a
   `NetworkManager` reference.

2. In `OpeningScene`, remove the `NetworkObject` component from `Game Manager`, `XRRig`,
   `Left Hand`, `Right Hand`.
3. In `GameScene`, remove the `NetworkObject` component from `XRRig`.
4. Save both scenes.

Do **not** touch the `NetworkObject` on `Player.prefab`. That one is the whole feature.

Sanity check afterwards — this must print exactly one hit per scene file, and it must be zero:

```powershell
Select-String -Path .\Assets\Scenes\*.unity -Pattern "GlobalObjectIdHash" | Select-Object Filename, LineNumber
```

### Step 2 — Create `StairsGame` and `ChasmGame`

In the Project window, select `Assets/Scenes/GameScene.unity` and press **Ctrl+D** twice. Rename
the copies `StairsGame.unity` and `ChasmGame.unity`.

Duplicating rather than building from scratch is deliberate — it carries the three MR settings
that fail *silently* if you miss one (`MRTemplate.md` §1.3C): `Main Camera` clear flags Solid
Color with `a: 0`, `RenderSettings.m_SkyboxMaterial: {fileID: 0}`, and the
`OVRPassthroughLayer` on `XRRig`. It also carries the wired `Menu Manager`, so the menu keeps
working in both games.

`GameScene` has `m_LightingDataAsset: {fileID: 0}` — no baked lighting — so the duplicates are
clean copies with nothing left pointing at `Assets/Scenes/SampleScene/`.

Put each game's content under that scene's existing empty `World Root`. `MenuControl.worldRoot`
is already wired to it and switches it off while the menu is open, so anything you park there
gets that behaviour for free.

**Keep `GameScene.unity`.** It is now the blank room template you duplicate for game three.
Nothing will reference it by name after Step 7.

### Step 3 — Add both scenes to the build's scene list

`File > Build Profiles > Scene List` (Unity 6 renamed Build Settings; the classic list is the
"shared scene list" on the profile). Result must be:

```
0  Assets/Scenes/OpeningScene.unity     <- must stay index 0, it is the lobby
1  Assets/Scenes/StairsGame.unity
2  Assets/Scenes/ChasmGame.unity
3  Assets/Scenes/GameScene.unity        <- optional; harmless to keep enabled
```

**A scene the server tries to load must be in this list, in every build.** If it is missing,
`NetworkSceneManager.LoadScene` returns `SceneEventProgressStatus.InvalidSceneName` and nothing
happens — no exception, no client-side error. The code in Step 5 logs that status explicitly
because it is the single most common way this feature "silently does nothing".

### Step 4 — New file: `Assets/Scripts/GameRoutes.cs`

One table, so a menu key, a scene name and the default game can never drift apart.

```csharp
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

    static readonly Dictionary<string, string> SceneByKey = new Dictionary<string, string>
    {
        { "Stairs", "StairsGame" },
        { "Chasms", "ChasmGame"  },
    };

    public static string DefaultScene => SceneByKey[DefaultGameKey];

    public static bool IsGameKey(string keyName) => SceneByKey.ContainsKey(keyName ?? "");

    public static bool TryGetScene(string gameKey, out string sceneName) =>
        SceneByKey.TryGetValue(gameKey ?? "", out sceneName);
}
```

### Step 5 — New file: `Assets/Scripts/GameSelector.cs`, added to `Player.prefab`

This is the mechanism for requirement 3.

**Why it lives on the player prefab.** A `ServerRpc` has to be sent from a `NetworkBehaviour` the
calling client owns. Every client owns exactly one `Player`, Netcode spawns it automatically, and
it is one of the few objects that stays alive *through* the scene load it is about to trigger. A
scene object would die halfway through its own scene change.

```csharp
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
```

**Editor step:** open `Assets/Prefabs/Player.prefab`, add `GameSelector` to the root `Player`
object, save. There is nothing to wire in the Inspector.

`[ServerRpc]` defaults to `RequireOwnership = true`, which is what you want here — a client can
only ever speak through the `Player` it owns, so nobody can spoof another player's request.

`string` as an RPC parameter is fine; `PlayerControls.SetPlayerNameServerRpc(string name)`
(`PlayerControls.cs:163-167`) already does it in this project.

### Step 6 — `RelayVivox.cs`: make hosting awaitable, and surface failure

Two changes. `StartRelayAndVivox` must be awaitable so the host can load the scene *after*
`StartHost()`, and `CreateRelay` must stop swallowing its exception — today a failed allocation
still fell through to `LoadScene`, dumping the user into an empty room with no session.

```csharp
// Assets/Scripts/RelayVivox.cs:32
-public async void StartRelayAndVivox(string userDisplayName)
+public async Task StartRelayAndVivox(string userDisplayName)
 {
     myUserDisplayName = userDisplayName;
     await CreateRelay();
     startVivoxVoice(userDisplayName, relayRoomCode);
 }
```

```csharp
// Assets/Scripts/RelayVivox.cs:68-71 — inside CreateRelay
 catch (RelayServiceException e)
 {
     Debug.Log(e);
+    throw;          // GameController has to know the room was never created.
 }
```

`JoinRelay` already rethrows (`RelayVivox.cs:102`), which is why the join path can re-prompt.
This makes the two paths behave the same way.

### Step 7 — `GameController.cs`: hand scene loading to Netcode

This is requirement 2. Replace the trigger-up block (`GameController.cs:124-144`) with:

```csharp
if (inputs.RightMainTriggerUp)
{
    if (pressedKey == null)
    {
        return;                       // trigger released without ever touching a key
    }

    pressedKey.MakeSmaller();

    if (pressedKey.keyName == "Enter" && joinedRelay)
    {
        if (joinCode == "" && nickName != "")
        {
            HostNewRoom();
        }
        else if (nickName != "")
        {
            TryToJoinRelayVivox();
        }
    }
}
```

(The null guard is a real fix, not tidying: line 130 dereferences `pressedKey.keyName` after a
`pressedKey != null` check on line 126 that does not guard it.)

Then replace the two `SceneManager.LoadScene("GameScene")` calls:

```csharp
/// <summary>
/// Host path. StartHost() has to complete before the scene can be loaded, because
/// NetworkManager.SceneManager does not exist until the session is running.
/// </summary>
private async void HostNewRoom()
{
    instructions.SetText("Creating room...");

    try
    {
        await relayVivoxStarter.StartRelayAndVivox(nickName);
    }
    catch (RelayServiceException)
    {
        instructions.SetText("Could not create a room. Press Enter to try again.");
        return;
    }

    if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer)
    {
        instructions.SetText("Could not create a room. Press Enter to try again.");
        return;
    }

    // Server-driven. This is what puts the host — and every client that joins later,
    // via Netcode's synchronization — into StairsGame.
    GameSelector.LoadGameScene(GameRoutes.DefaultScene);
}

private async void TryToJoinRelayVivox()
{
    try
    {
        await relayVivoxStarter.JoinRelayAndVivox(nickName, joinCode);
    }
    catch (RelayServiceException)
    {
        Debug.Log("Caught exception");
        instructions.SetText("Wrong Room Code");
        instructionsSubfield.gameObject.SetActive(true);
        instructionsSubfield.SetText("Please enter a new code or press Enter to start a new room");
        instructions.color = new Color(0.22f, .94f, 1f, 1);
        instructions.transform.Translate(new Vector3(0, .07f, 0));
        inputField.GetComponent<TMP_InputField>().placeholder.gameObject
            .GetComponent<TextMeshProUGUI>().text = "Room Code...";
        joinedRelay = false;
        joinCode = "";
        inputField.GetComponent<TMP_InputField>().text = joinCode;
        return;
    }

    // No LoadScene here, on purpose. Netcode synchronizes this client into whatever scene
    // the host already has open — which may be StairsGame or ChasmGame, depending on what
    // the room is playing right now. Loading a scene here would fight that.
    instructions.SetText("Joining room...");
    instructionsSubfield.gameObject.SetActive(false);
}
```

`using UnityEngine.SceneManagement;` is now unused in this file — the compiler will not complain,
but delete it so nobody reintroduces a direct `LoadScene`.

**The join path is the one people get wrong.** It genuinely does nothing after `StartClient()`.
The scene change arrives from the server as part of client synchronization. If it never arrives,
the bug is in the connection, not in the missing `LoadScene`.

### Step 8 — `MenuControl.cs`: wire the two keys

```csharp
// Assets/Scripts/MenuControl.cs — in HandleKey, above `default:`
case "Stairs":
case "Chasms":
    RequestGame(keyName);
    CloseMenu();
    break;
```

and add:

```csharp
/// <summary>
/// Ask the server to move the whole room into a game. Any player may do this, not just the
/// room owner — the request is validated server-side against GameRoutes.
/// </summary>
private void RequestGame(string gameKey)
{
    // myPlayer is set by PlayerControls.Setup(). After a scene switch this MenuControl is a
    // brand-new instance in a brand-new scene, so fall back to asking Netcode directly rather
    // than depending on rebind order.
    if (myPlayer == null)
    {
        NetworkManager nm = NetworkManager.Singleton;
        if (nm != null && nm.LocalClient != null && nm.LocalClient.PlayerObject != null)
        {
            myPlayer = nm.LocalClient.PlayerObject.GetComponent<PlayerControls>();
        }
    }

    GameSelector selector = myPlayer != null ? myPlayer.GetComponent<GameSelector>() : null;
    if (selector == null)
    {
        Debug.LogWarning("MenuControl: no local player yet, cannot switch to '" + gameKey + "'.");
        return;
    }

    selector.RequestGame(gameKey);
}
```

Add `using Unity.Netcode;` at the top of the file.

The `default:` case body stays as it is — it is the correct behaviour for a key with no game
behind it. Update its comment though: `MenuControl.cs:94-95` currently reads *"Stairs and Chasms
land here"*, which stops being true the moment you add the two cases above it.

### Step 9 — `PlayerControls.cs`: rebind on every scene change

This is requirement 1, and the reason requirement 3 does not break it.

Split `OnNetworkSpawn` into "things that belong to the Player prefab" (once) and "things that
belong to the current scene" (every scene load).

```csharp
using UnityEngine;
using UnityEngine.SceneManagement;      // add
using Unity.Netcode;
using TMPro;
using Unity.Collections;
```

```csharp
public override void OnNetworkSpawn()
{
    // --- Prefab-local. Resolved once; these children never go away. ---
    usernameTransform = transform.GetChild(1);
    playerLeft        = transform.GetChild(2);
    playerRight       = transform.GetChild(3);
    face              = transform.GetChild(4);
    username          = usernameTransform.GetComponent<TextMeshPro>();

    username.SetText(playerName.Value.ToString());
    playerName.OnValueChanged += HandleNameChanged;

    // RelayVivox rides the NetworkManager object, which is DontDestroyOnLoad, so this one
    // survives scene changes too. GameObject.Find does search the DontDestroyOnLoad scene.
    relayVivoxInfo = FindComponent<RelayVivox>("Network Manager");

    if (IsOwner)
    {
        if (relayVivoxInfo != null)
        {
            SetRoomOwnerServerRpc(relayVivoxInfo.roomOwner);
            SetPlayerNameServerRpc(Clamp(relayVivoxInfo.myUserDisplayName));
        }

        // You are inside your own avatar; do not render it for yourself.
        for (int i = 0; i < transform.childCount; i++)
        {
            transform.GetChild(i).gameObject.SetActive(false);
        }
    }

    // --- Scene-local. Re-resolved after every game switch. ---
    BindToScene();
    SceneManager.activeSceneChanged += HandleActiveSceneChanged;
}

public override void OnNetworkDespawn()
{
    SceneManager.activeSceneChanged -= HandleActiveSceneChanged;
    playerName.OnValueChanged -= HandleNameChanged;
}

private void HandleNameChanged(FixedString32Bytes oldVal, FixedString32Bytes newVal)
{
    username.SetText(newVal.ToString());
}

private void HandleActiveSceneChanged(Scene from, Scene to)
{
    BindToScene();
}

/// <summary>
/// Re-resolve everything that lives in the current scene. The Player survives the scene loads
/// GameSelector triggers; the rig, the camera, the hands and the Menu Manager do not. Without
/// this, myCam is a destroyed object after the first game switch, PlayerControls.Update()
/// early-returns forever, and every remote avatar freezes with its hands and face at the
/// origin while the tornado body keeps tracking. That asymmetry is the tell.
/// </summary>
private void BindToScene()
{
    inputs     = FindComponent<InputReader>("Input Reader", required: false);
    myCam      = Camera.main != null ? Camera.main.transform : null;
    localLeft  = FindTransform("Left Hand");
    localRight = FindTransform("Right Hand");
    rig        = FindTransform("XRRig");

    if (!IsOwner)
    {
        return;
    }

    // Both of these are new instances in the new scene and have never heard of this player.
    CameraController2 mainRig = FindComponent<CameraController2>("XRRig");
    if (mainRig != null)
    {
        mainRig.Setup(this);
    }

    MenuControl menu = FindComponent<MenuControl>("Menu Manager", required: false);
    if (menu != null)
    {
        menu.Setup(this);
    }
}

/// <summary>
/// playerName is a FixedString32Bytes — 29 bytes of UTF-8. The lobby keyboard has no length
/// limit, so an over-long username throws inside SetPlayerNameServerRpc on the server and
/// takes the name down for everybody.
/// </summary>
private static string Clamp(string name)
{
    if (string.IsNullOrEmpty(name))
    {
        return "player";
    }
    return name.Length <= 29 ? name : name.Substring(0, 29);
}
```

Also make `FindTransform` log a **warning**, not an error (`PlayerControls.cs:137`). `BindToScene`
runs during the lobby → game transition as well, and a one-frame miss there is normal.

Everything else in the file — `Update()`, the ServerRpcs, `FindComponent` — stays as it is.

### Step 10 — Avatar checks in the Editor

Requirement 1 is code-complete after Step 9. These are the serialized values it depends on;
confirm each before you conclude something is still broken.

1. **Child order on `Player.prefab` must stay** `tornado, Username, PlayerLeft, PlayerRight,
   mainFace`. `PlayerControls` resolves children by index. Reordering them in the Hierarchy
   compiles fine and produces a scrambled avatar. If you have five minutes, replace the four
   `GetChild(n)` calls with `transform.Find("Username")` etc. — `MRTemplate.md` §5 lists this
   exact pattern as the project's most common silent breakage.
2. **`NetworkConfig.PlayerPrefab`** is `Player.prefab` — already correct
   (`OpeningScene.unity:499`).
3. **`DefaultNetworkPrefabs.asset`** contains `Player` (guid `9243d030…`) — already correct.
4. **`SpawnWithObservers: 1`** on the `Player` `NetworkObject` — already correct
   (`Player.prefab:262`). If this were off, nobody would see anybody.
5. **Materials.** `tornado.fbx` and `face.obj` both import with `materialLocation: 1` — materials
   embedded in the model, not external `.mat` files. The `Assets/Materials/*.mat` deletions in the
   current working tree therefore did **not** touch the avatar. But embedded FBX/OBJ materials can
   import against the Built-in shader in a URP project and render magenta. Look at a *remote*
   avatar in play mode; if it is magenta, extract the materials
   (`Inspector > Materials > Extract Materials…`) and set them to `Universal Render Pipeline/Lit`.
6. **`Assets/tornado.fbx`, `Assets/face.obj`, `Assets/Oculus/Interaction/Runtime/Meshes/OculusHandPinchArrowBlended.fbx`,
   `LiberationSans SDF`** all still exist. Verified.
7. **Optional:** tick `Active Scene Synchronization` on `Player.prefab`'s `NetworkObject`
   (currently `0`). Without it the players sit under `DontDestroyOnLoad` in the Hierarchy after a
   switch instead of in the new scene. It does not affect visibility either way — it just makes
   the Hierarchy readable while you are debugging.

---

## 4. Test plan

The whole app is playable without a headset — `InputReader` falls back to the keyboard whenever
no XR controller is found. Relevant keys: **X** opens the menu, **`.`** is the right trigger,
**E** is the left joystick button (recenter), **U/H/J/K** are the left joystick (move and turn),
**I** is the right joystick button (clear the debug log).

Locomotion is on the **left** controller as of the ring change: the left stick moves and snap-turns,
and clicking it recentres you on your place around the board. The right stick no longer steers.

Two clients are mandatory; requirements 1 and 3 are both invisible with one. Use Multiplayer Play
Mode, two Editor instances via ParrelSync, or Editor + device.

| # | Test | Pass |
|---|---|---|
| 1 | Client A: start from `OpeningScene`, Enter with an empty room code, pick a username | A lands in **`StairsGame`**, not `GameScene` |
| 2 | Client B: enter A's room code and a different username | B lands in `StairsGame` **without** `GameController` calling `LoadScene` |
| 3 | Look at each other | Tornado body, face, both hands and the correct username, all tracking. The username billboards toward the viewer. Neither player sees their own avatar |
| 4 | **B** (not the host) presses X → Chasms | **Both** clients load `ChasmGame`. Both avatars are still there and still tracking |
| 5 | **A** presses X → Stairs | Both return to `StairsGame`. Avatars still intact — this is the test that catches a missing rebind (§2.2), because it is the *second* switch |
| 6 | Either client presses the game already running | Nothing happens. Console: `the room is already in '…'` |
| 7 | Client C joins mid-session while the room is in `ChasmGame` | C synchronizes straight into `ChasmGame`, and A and B see C's avatar |
| 8 | Both clients open the menu, press different games within the same second | One load runs. Console: `ignoring '…', a scene load is already running` |
| 9 | Voice | Vivox is unaffected — its channel is the room code, not the scene. Talk across a switch to confirm |
| 10 | Passthrough | `PassthroughController` keeps its state in a `static`, so a user's passthrough choice survives every switch. Verify it does |

Then on device. Per `MRTemplate.md` §4.8 the MR conversion has still never run on a headset, and
passthrough failures look identical to a black background in the Editor.

---

## 5. Traps

- **`Camera.main` needs the `MainCamera` tag.** If a duplicated scene's camera loses it,
  `BindToScene` sets `myCam = null` and every remote avatar freezes — with no error. That is the
  same symptom as §2.2 and it will send you back to code that is already correct.
- ~~**The rig resets to the scene's authored transform on every switch.**~~ No longer true.
  `PlayerControls.BindToScene` now re-places the rig on this player's slot around the board after
  every switch (`PlayerRing` holds the geometry), and clicking the **left** joystick returns you
  to that same slot. Recentre is still per-client by design (`CameraController2.Recenter`): the
  shared world never moves, so every networked value stays in world space.
- **The menu disappears mid-interaction when someone else switches.** That is requirement 3 working
  as specified. Say so in the UI if it confuses playtesters — a "Player X chose Chasms" line via a
  `ClientRpc` is the obvious follow-up.
- **Picking the game you are already in does nothing.** Your requirement said "choose a *different*
  game", so that is the default. If you want it to restart the game instead, delete the
  `SceneManager.GetActiveScene().name == sceneName` guard in `GameSelector.LoadGameScene` — but
  keep the `s_SwitchInProgress` guard, or a held trigger will queue loads.
- **`NetworkReconnectHandler.cs` and `PersistentObject.cs` are attached to nothing.** Do not attach
  `NetworkReconnectHandler` to the `Network Manager` to "harden" this work:
  `NetworkReconnectHandler.cs:23-24` calls `Shutdown()` then `StartClient()` with whatever relay
  data is already on the transport, in a 5-second loop, and it would fire during a slow scene load.
  Either delete both scripts or leave them dormant.
- **`RelayVivox.CreateRelay` allocates for 12 players** (`RelayVivox.cs:51`). That is the room cap,
  unrelated to scenes, but it is the number to change if the board game needs a different one.
- **Do not reintroduce `UnityEngine.SceneManagement.SceneManager.LoadScene` anywhere.** After this
  work, `GameSelector.LoadGameScene` is the only place the shared scene changes. A grep that comes
  back with exactly one hit — in `GameSelector` — is the invariant worth keeping:

  ```powershell
  Select-String -Path .\Assets\Scripts\*.cs -Pattern "SceneManager.LoadScene"
  ```

---

## 6. Files touched

| File | Change |
|---|---|
| `Assets/Scripts/GameRoutes.cs` | **new** — key → scene table |
| `Assets/Scripts/GameSelector.cs` | **new** — `ServerRpc` + the one server-side `LoadScene` |
| `Assets/Scripts/GameController.cs` | `NetworkBehaviour` → `MonoBehaviour`; `HostNewRoom()`; join path no longer loads a scene; `pressedKey` null guard |
| `Assets/Scripts/RelayVivox.cs` | `StartRelayAndVivox` returns `Task`; `CreateRelay` rethrows |
| `Assets/Scripts/MenuControl.cs` | `Stairs` / `Chasms` cases + `RequestGame` |
| `Assets/Scripts/PlayerControls.cs` | `BindToScene()` on `activeSceneChanged`; username clamp; event unsubscribe in `OnNetworkDespawn` |
| `Assets/Prefabs/Player.prefab` | add `GameSelector` |
| `Assets/Scenes/StairsGame.unity` | **new** — duplicate of `GameScene` |
| `Assets/Scenes/ChasmGame.unity` | **new** — duplicate of `GameScene` |
| `Assets/Scenes/OpeningScene.unity` | remove 4 `NetworkObject`s |
| `Assets/Scenes/GameScene.unity` | remove 1 `NetworkObject`; stays in the project as the blank room template |
| `ProjectSettings/EditorBuildSettings.asset` | add both new scenes |

---

## 7. Deliberately out of scope

Worth a follow-up, not worth blocking this on:

- **Telling players who changed the game.** A `ClientRpc` carrying the requester's `playerName`,
  shown on the `Debugger` TMP object that both game scenes already have.
- **Restricting game choice to the room owner.** `PlayerControls.roomOwner` (`PlayerControls.cs:27`)
  is already replicated and server-written, so `GameSelector.RequestGameServerRpc` could check it
  in one line. Your requirement says *any* user, so it does not today.
- **Highlighting the current game in `Menu1`.** `keyInfo.KeepOn()` exists for exactly this.
- **Replacing `GetChild(n)` and `GameObject.Find` throughout.** `MRTemplate.md` §5 has the list.
  Step 9 makes the `Find` calls run more often, which raises the cost of a rename — worth doing
  before the game content lands, not during.
- **Product identity.** `MRTemplate.md` §3.6: this still builds as `Math_Classroom_v8` /
  `com.CoolDeal.Math_Classroom_v6`. Change the Android application identifier before the first
  device install, not after.
