# LobbyUpdate.md — the store lobby, the room browser, and a room menu that scales

> **Status: APPLIED, steps 1–14. Step 15 is deliberately not done.** Read [§14](#14-how-it-came-out)
> first — it records where the code came out differently from the plan, which of the **verify** items
> were actually checked, and what has never been run. Written in the style of the rest of `docs/`:
> the plan with the evidence attached, so a claim can be checked rather than taken on trust. Facts
> marked **measured** were read out of the live Editor while this was written (Unity MCP bridge,
> Editor `6000.5.4f1`, working tree at `179de3f`).

Read [`CLAUDE.md`](../CLAUDE.md) first, and
[the shared systems doc](../Assets/Scripts/CLAUDE.md) — especially
[Session flow](../Assets/Scripts/CLAUDE.md#session-flow),
[The persistent rig](../Assets/Scripts/CLAUDE.md#the-persistent-rig) and
[Menus, pointer, keys](../Assets/Scripts/CLAUDE.md#menus-pointer-keys). This document is the delta on
top of those.

---

## 0. The short answers

**Does anything need to be installed to browse and join public rooms?** **No.** Unity Lobby is
already compiled into this project. `com.unity.services.multiplayer@2.2.4` folds Lobby, Relay and
Matchmaker into one assembly, and `MRBoardGame.Shared.asmdef` already references it — so
`Unity.Services.Lobbies.LobbyService`, `Models.Lobby` and `Models.DataObject` can be typed into a new
file under `Assets/Scripts/` today with no package change, no asmdef edit, and therefore no risk to
the join handshake. **Measured.**

**Does anything need to be installed to sell a game?** **Yes, exactly one thing, and only at the very
end.** `com.meta.xr.sdk.platform` is not in `Packages/manifest.json` and `Oculus.Platform.Core` does
not resolve. **Measured.** Unity IAP is not installed either — `Assets/Resources/BillingMode.json` is
a leftover from *Math Classroom* and is not evidence to the contrary. The whole of §4–§9 is built and
tested against a mock, and §10 swaps one class.

**Do the two canvases need Unity's `EventSystem` / `GraphicRaycaster`?** **No, and they should not
use them.** A ray-driven uGUI input module is ~250 lines of well-known-but-fiddly code that this
project has no other use for. Everything pressable here already has a working input path:
`PointerBeam` (on `Right Hand/Pointer`, order 24, after its own `Physics.SyncTransforms()`) publishes
`Origin` and `Direction`, and `pointerControl`'s trigger capsule reports `currentKey` for any collider
tagged `key`. **Measured.** So: **Canvas for pixels, colliders for presses.** uGUI draws the panel —
`Image`, `TextMeshProUGUI`, `RectMask2D`, layout — and each pressable row additionally carries a
`BoxCollider` + kinematic `Rigidbody` + tag `key` + `keyInfo`, which is exactly the widget the room
menu already clones. `ScrollTextWithJoystick` already proves a `ScrollRect` works with no
`EventSystem` at all (it writes `verticalNormalizedPosition` directly).

**Decisions this plan takes.** Each is a fork where a different answer changes the work, so each says
what would change.

| | Decision | If you disagree |
| --- | --- | --- |
| Public rooms | **Unity Lobby as a directory over the existing Relay flow.** The host publishes its Relay join code into a lobby; a browsing client reads the code out and then goes down `RelayVivox.JoinRelay(code)` completely unchanged. | The alternative is the Sessions API (`MultiplayerService.CreateSessionAsync().WithRelayNetwork()`), which replaces `RelayVivox` wholesale — including the `connectInFlight` latch, the 15 s watchdog, the `dtls` match and the Shutdown-before-retry fix. That re-opens [`quest_networking_plan.md`](quest_networking_plan.md) in new code. Not worth it. |
| Paid games | **A `IEntitlementService` interface with a PlayerPrefs mock**, whose method list is shaped one-for-one to `Oculus.Platform.IAP`. | Nothing else — this is what was asked for. |
| Free games | **Explicitly added, not auto-granted.** The Play canvas must then never be a dead end: a disabled button always says *why*, and "Join Private Room" works with an empty library. | Seeding every free product on first run is a two-line change in `StoreService.Initialize` if the empty-library first run reads badly in a headset. |
| In-room menu | **Convert to the panel toolkit** (§9). It collapses `Menu1`/`Menu2` into one prefab, removes the `ComfortableColumnKeys = 4` ceiling, and makes `Passthrough` reachable. | Keeping the 3D key column means re-solving the column overflow anyway, in a second layout system. |
| The wrist `SpawnMenu` | **Untouched.** It is Plateau's, it has its own dispatcher, and it is not in the way. | — |

---

## 1. What is in the lobby now

`OpeningScene`, six roots. **Measured:**

```
EventSystem            [EventSystem, StandaloneInputModule, BaseInput]   <- drives nothing
Network Manager        [RelayVivox, NetworkManager, UnityTransport, BoardAnchor, NetworkProbe]
Game Manager           [GameController, MicPermissions]
Keyboard               Row1 / Row2 / Row3 / Row 4 — 40 keys, each [BoxCollider, keyInfo, Rigidbody]
Text Input             [Canvas WorldSpace, worldCamera = none, scale 0.01, size 200.7 x 57]
  InputField (TMP), Instructions, Instructions (1)
PersistentRig          [PersistentObject] > Directional Light, XRRig, Input Reader, Menu Manager
```

The flow is one state machine inside `GameController.Update()`: read `pointer.currentLetter` on
right-trigger down, append it to `joinCode` until Enter, then append to `nickName` until Enter, then
either `HostNewRoom()` (empty code) or `TryToJoinRelayVivox()`. Everything downstream of those two
calls is good and **this plan does not touch it** — that is the single most important property of
what follows. The rewrite replaces *what calls them*, not what they do.

Three existing facts the new lobby has to respect:

- **`PersistentRig` is in this scene and `MenuControl` comes with it.** `X` opens the room menu in the
  lobby today. It resolves no local player, warns, and does nothing useful. With a library-filtered
  menu (§9) it would open empty. Suppress it in the lobby — one line in `MenuControl.Update`.
- **`GameController.Start()` and `MenuControl.ApplyPointerDefault()` both write the pointer's active
  state on load and must keep agreeing.** They already do, deliberately; see the shared doc. The new
  lobby controller inherits that obligation.
- **The rig rests at its authored pose in the lobby.** `CameraController2.PlaceAtRingSlot` early-outs
  for non-game scenes, and `PersistentRig.prefab`'s root is at `(0, 0, -10)`. The panels must be
  placed in front of *that*, the way `Keyboard` is today — not in front of the world origin.

---

## 2. The shape being built

Three surfaces, two of them in the lobby and one in the room.

```
                       . . . . . . . . . . . . . . . . . . . . . . . .
    LOBBY (OpeningScene)                                            .
                                                                    .
    +--------------------------------+   +----------------------+   .
    |  LIBRARY                       |   |  PLAY                |   .
    |                                |   |                      |   .
    |  [img] Stairs      In Library  |   |  New Private Room    |   .
    |  [img] Chasms      Free  Add   |   |  Join Private Room   |   .
    |  [img] BASH        $4.99       |   |  ------------------  |   .
    |  [img] ...              ^ v    |   |  New Public Room >   |   .
    |                                |   |  Browse Public   >   |   .
    |  +--------------------------+  |   |  ------------------  |   .
    |  | detail: blurb + rules    |  |   |  You are: SAM  [edit]|   .
    |  +--------------------------+  |   +----------------------+   .
    +--------------------------------+        angled ~35 deg        .
              dead ahead, ~1.6 m               to the right         .
                                                                    .
    . . . . . . . . . . . . . . . . . . . . . . . . . . . . . . . . .

    ROOM (StairsGame / ChasmGame / BashGame ...)     X opens:

                            +----------------------------+
                            |  ROOM MENU                 |
                            |  Stairs                    |   <- one row per game the
                            |  Chasms                    |      ROOM's library allows
                            |  BASH                 ^ v  |
                            |  --------------------------|
                            |  Place Anchor  Passthrough |
                            |  Voice Chat: OFF   (host)  |
                            |  --------------------------|
                            |  End Turn      (the loaded |
                            |                 game's own)|
                            +----------------------------+
```

All three are built from the same two prefabs and the same two scripts (§3). That is the point of
doing the panel toolkit first: the room menu is not a fourth layout system, it is the same list with
different rows.

New folders, both inside `MRBoardGame.Shared` (a subfolder with no `.asmdef` of its own stays in the
parent assembly, exactly as `Assets/Scripts/Games/` does):

```
Assets/Scripts/Ui/        Panel, ScrollList, PanelRow, CodePad
Assets/Scripts/Store/     IEntitlementService, MockEntitlementService, StoreService, StoreProduct view
Assets/Scripts/Lobby/     LobbyController, RoomDirectory, RoomOptions
```

---

## 3. The panel toolkit

### Step 1 — make `keyInfo` renderer-agnostic

`keyInfo` is already the right widget: a `keyName`, an on/off material, a label, `MakeBigger` /
`MakeSmaller`, and `overrideNameChange` so a runtime-built key's label is not overwritten by its own
`Start()`. `MenuControl.CloneKey` is the pattern to copy. Two things stop it living on a Canvas:

1. All four material methods do `GetComponent<MeshRenderer>().material = currentMaterial`
   (`keyInfo.cs:48-78`). **Measured.** A UI row has an `Image`, not a `MeshRenderer`.
2. `keyLabel` is typed `TextMeshPro`. A UI row's label is `TextMeshProUGUI`.

Change: cache the renderer in `Awake`, add an optional `UnityEngine.UI.Graphic targetGraphic` plus
`onColor` / `offColor`, and route all four methods through one `Apply(bool on)`. Widen `keyLabel` from
`TextMeshPro` to `TMP_Text` — both existing types derive from it and both have `SetText`.

> **Verify.** Widening a serialized reference field's type should keep every existing assignment,
> because the value is a `PPtr` and `TextMeshPro` is assignable to `TMP_Text`. After the change, open
> `Menu1.prefab`, `Menu2.prefab` and `PersistentRig.prefab > … > SpawnMenu` and confirm every
> `keyInfo.keyLabel` slot is still filled. A serialized value lost here is silent — see
> [Conventions that break silently](../CLAUDE.md#conventions-that-break-silently).

Two constraints this creates, both worth writing into the new prefab's comments:

- `MakeBigger` multiplies `transform.localScale` by 1.2. On a `RectTransform` that is fine **only if
  nothing else writes the scale**, so UI rows are positioned manually and **must not** sit under a
  `LayoutGroup`. `MenuControl.BuildKeys` already positions its keys by hand; this is the same rule.
- Trigger callbacks need a `Rigidbody` on the *other* collider, which is why every existing key has
  one. Rows need `isKinematic = true` and `useGravity = false` or the list will fall out of the panel.

**Done when:** an existing 3D menu key and a new Canvas-backed row both highlight on hover and grow on
press, from the same component.

### Step 2 — `Panel.prefab` and `PanelRow.prefab`

`Panel`: a World Space `Canvas` with `worldCamera` left null (which is what `Text Input` already does
— **measured**), a background `Image`, a `TextMeshProUGUI` header, and a `RectMask2D` viewport.

**Fix the unit convention once, here: 1 UI unit = 1 mm, `localScale = 0.001`.** A 1.2 m × 0.8 m panel
is then `sizeDelta = (1200, 800)` and a row is 96 units ≈ 9.6 cm tall — legible at 1.6 m. The project
currently has two conventions in play (`Text Input` at 0.01, `MenuControl.BuildRulesPanel` at 0.002,
both **measured**), which is exactly how panels end up subtly different sizes.

`PanelRow`: `Image` background, `Image` thumbnail, two `TextMeshProUGUI` (title, subtitle), one
`TextMeshProUGUI` for the right-hand state, plus `BoxCollider` + kinematic `Rigidbody` + tag `key` +
`keyInfo`. The collider is sized in **UI units** (e.g. `1180 × 90 × 10`); the canvas scale converts.

**Done when:** a `Panel` with three hand-placed `PanelRow`s can be pointed at and pressed in the
Editor with `.` (the right-trigger keyboard fallback), and `MenuControl`-style press-down/act-up works.

### Step 3 — `ScrollList`

The scrollable list, built as **N fixed row slots that never move outside the viewport**, with the
scroll offset changing which data each slot shows. That is what makes colliders safe: a `RectMask2D`
clips *pixels*, not colliders, so a naive "long content, scroll the content" list lets you press rows
you cannot see. A recycling list has no rows outside the viewport to press.

```csharp
public class ScrollList : MonoBehaviour
{
    public RectTransform viewport;
    public PanelRow rowTemplate;      // inactive in the prefab, cloned N times — MenuControl's idiom
    public int visibleRows = 6;
    public float rowHeight = 96f;
    public InputReader inputs;

    public void SetData(IReadOnlyList<RowData> rows);   // rebinds, clamps the offset
    public void Scroll(int delta);                       // by whole rows
}
```

- **Right-joystick Y scrolls**, the `ScrollTextWithJoystick` idiom (`inputs.rightJoystick.y`), with a
  repeat delay (~0.25 s, then ~0.08 s) so one flick moves one row rather than twelve.
- Generate `▲` / `▼` keys and an `n / m` counter only when `rows.Count > visibleRows`.
- **The list refuses to scroll while the right trigger is held.** Without this, a press begun on row 3
  and released after a scroll acts on whatever row 3 now shows — the key object is the same, its
  `keyName` is not. One `if` removes the whole class of bug.
- Row `keyName`s are the row's **stable id** (`GameModule.gameKey`, a lobby id, an action key), never
  a slot index.

**Done when:** a 20-item list scrolls smoothly in the Editor, the beam shortens correctly onto rows,
and a press held across a scroll attempt still acts on the row it started on.

---

## 4. Games as products

### Step 4 — store fields on `GameModule`

Not a second asset: one asset per game is the existing contract and it is a good one.

```csharp
[Header("Store")]
public string productId  = "";      // stable forever, NEVER reused. "mrbg.stairs"
public int    libraryBit = -1;      // 0..63, stable forever, NEVER reused
public string displayName = "";     // store name; menuLabel stays the short key label
[TextArea] public string blurb = "";
public Sprite thumbnail;
public bool   isPaid = false;
public string metaSku = "";         // the Meta add-on SKU; empty when free
public string mockPriceLabel = "";  // MOCK ONLY. The real price comes from the platform — see §5
public int    minPlayers = 2, maxPlayers = 12;
```

**Why two identities.** `productId` is what a player's saved library is keyed by, and that library
outlives app updates; `libraryBit` is what travels on the wire, where a `ulong` mask is 8 bytes
against a `NetworkList<FixedString32Bytes>`. Neither can be the catalog index: reordering
`GameCatalog.games` between two app versions would silently hand a player a different game than the
one they bought. Both are authored on the same asset, so the mapping lives in one place.

`maxPlayers` is bounded by the Relay allocation and the ring, both **12**
(`RelayVivox.CreateRelay` → `CreateAllocationAsync(12)`, `PlayerRing` 12 slots).

Existing modules to fill in — **measured**: `Assets/Games/Stairs/StairsModule.asset` (`Stairs` →
`StairsGame`, the catalog default), `Assets/Games/Plateau/PlateauModule.asset` (`Chasms` →
`ChasmGame`), `Assets/Games/Bash/BashModule.asset` (`BASH` → `BashGame`). All three are free, per the
request, so `isPaid = false` and `metaSku = ""` on all three. Give one of them a fake paid twin during
development so the purchase path is exercised — a fourth catalog row pointing at an existing scene is
enough, and it is deleted before ship.

### Step 5 — `GameCatalogValidator` (Editor)

`GameModule.cs:12` already tells the reader that "GameCatalogValidator complains if you forget the
second step". **There is no such file** — `Assets/Editor/` holds only `MRPassthroughSetup.cs` and
`NetworkPrefabListGuard.cs`. **Measured.** This step makes the comment true and is the cheapest
insurance in the plan. It fails the build (the way `NetworkPrefabListGuard` does) on:

- a `GameModule` asset that is not in `Resources/GameCatalog.asset`;
- a duplicate or empty `productId`; a duplicate `libraryBit`, or one outside `0..63`;
- `isPaid` with an empty `metaSku`;
- a `sceneName` absent from `EditorBuildSettings.scenes` — today that failure surfaces only as a
  `Debug.LogError` from `GameSelector` at runtime, in a headset.

---

## 5. Entitlements, and the seam the Meta SDK plugs into

### Step 6 — `IEntitlementService`, the mock, and `StoreService`

```
Assets/Scripts/Store/
  IEntitlementService.cs      the seam
  MockEntitlementService.cs   PlayerPrefs + a settings asset
  StoreService.cs             static facade; owns the ulong mask; raises Changed
  MetaEntitlementService.cs   §10, behind #if MRBG_META_PLATFORM
```

The interface is shaped to the Meta calls deliberately, so §10 is a constructor change rather than a
redesign:

| `IEntitlementService` | Mock | Meta Platform SDK |
| --- | --- | --- |
| `Task<bool> InitializeAsync()` | read `PlayerPrefs` | `Core.AsyncInitialize()` → `Entitlements.IsUserEntitledToApplication()` → `IAP.GetViewerPurchases()` |
| `Task RefreshPricesAsync(IEnumerable<string> skus)` | copy `mockPriceLabel` | `IAP.GetProductsBySKU(skus)` — **the formatted, localized price** |
| `Task<PurchaseResult> PurchaseAsync(string productId)` | fake latency, then write | `IAP.LaunchCheckoutFlow(sku)` |
| `Task<bool> GrantFreeAsync(string productId)` | write | write locally; free products have no SKU |
| `Task RefreshOwnedAsync()` | re-read | `IAP.GetViewerPurchases()` |
| `IReadOnlyCollection<string> Owned` | the set | the set |
| `event Action Changed` | — | — |
| `string DisplayName` | last-used name / `"Player"` | `Users.GetLoggedInUser()` |

**One rule keeps this honest: nothing outside `Assets/Scripts/Store/` may name `Oculus.Platform` or
`PlayerPrefs`.** Everything else asks `StoreService.Owns(module)` / `StoreService.OwnedMask`. This is
the same discipline `IGameSession` enforces between shared code and the games, and for the same
reason.

`PurchaseResult` must distinguish `Succeeded / Cancelled / Failed(reason) / AlreadyOwned` — Meta
returns all four and a UI that collapses them shows "purchase failed" to someone who pressed Back.

**Mock specifics.** Key `MRBG.owned.<productId>` = `"1"`. A `MockStoreSettings` asset in `Resources/`
carrying: simulated latency, a **force-failure** toggle, a **force-cancel** toggle, and a *clear
library* button. The failure paths are the ones you otherwise cannot test, and they are the ones a
store reviewer will find.

**Prices are a store-compliance point, not a display detail.** Meta returns a formatted price in the
user's currency; a hardcoded `"$4.99"` is wrong for most of the planet and is the sort of thing
review catches. `mockPriceLabel` exists so the mock can render *something*, and
`MetaEntitlementService` must overwrite it — never fall back to it.

**Done when:** with the Editor's keyboard fallback you can add a free game, "buy" a paid one, watch a
forced failure, restart Play mode, and see the library persist.

---

## 6. The Library canvas

### Step 7 — the store panel

A `Panel` + `ScrollList` where each row is a `GameModule`, plus a detail pane on the right of the same
canvas.

| Row state | Right-hand cell | Pressable |
| --- | --- | --- |
| owned | `In Library` ✓ | selects it (shows detail) only |
| free, not owned | `Free — Add` | `GrantFreeAsync` |
| paid, not owned | the platform price | `PurchaseAsync` |
| in flight | `…` | no |
| failed | `Retry` + a message under the header | yes |

The detail pane shows `blurb`, `minPlayers–maxPlayers`, and **`GameModule.rulesText`** — the same
`TextAsset` the in-room rules panel already reads (`MenuControl.BuildRulesPanel`). One asset, two
readers, no second copy of the rules to drift.

Rows are built from `GameCatalog.Instance.games`, in catalog order. **Nothing here names a game** —
same contract as `MenuControl`.

**Done when:** the panel renders the three real modules with thumbnails, scrolls, buys the fake paid
twin, and reflects `StoreService.Changed` without being rebuilt.

---

## 7. The Play canvas

### Step 8 — a username without a QWERTY keyboard

Sources, in order: `IEntitlementService.DisplayName` (the Meta name once §10 lands) → the last name
used (`PlayerPrefs`) → typed on the `CodePad`. The panel shows `You are: SAM [edit]`.

`playerName` is a `FixedString32Bytes` — 29 bytes of UTF-8 — and `PlayerControls` already clamps
before the ServerRpc. **That clamp matters more now**, because a Meta display name is not
length-limited by a lobby keyboard the way a typed one was.

### Step 9 — private rooms, and deleting the keyboard

Two rows on the Play panel:

- **New Private Room** → `GameController.HostNewRoom()`, unchanged.
- **Join Private Room** → opens `CodePad`, a compact 6 × 6 grid of `A–Z 0–9` plus `Back`, `Clear`,
  `Join`, sized for a **6-character** Relay join code → `GameController.TryToJoinRelayVivox()`,
  unchanged.

> **Verify.** Confirm the Relay join-code alphabet by generating a handful of codes and looking at
> them, before laying out the pad. A code containing a character the pad cannot type is unjoinable
> and there is no error that says so. The failure path is already handled — a code that does not exist
> throws `RelayServiceException` and lands in `ShowJoinFailed` — so an over-large alphabet is the safe
> direction to be wrong in.

**This is where `Keyboard` (40 keys) and the keyboard half of `GameController.Update()` are deleted.**
The state machine goes; `HostNewRoom`, `TryToJoinRelayVivox`, `WatchJoin`, `HandleClientDisconnect`,
`HandleTransportFailure` and `ShowJoinFailed` all stay exactly as they are. `ShowJoinFailed`'s
relative 7 cm nudge of the instructions transform and its `joinedRelay` idempotence guard go with the
keyboard — the new panel shows the message in a status line instead, which removes that whole
hazard.

While the rewrite is in flight, `GameController` should also drop its four direct serialized
references into the rig (`inputs`, `rh`, `lh`, `pointer`) in favour of the `Find`-by-name /
`Bind()` convention everything else uses. The shared doc already calls those out as
[a liability rather than a model to copy](../Assets/Scripts/CLAUDE.md#the-persistent-rig); this is the
one commit where changing them is free.

### Step 10 — the name and the library arrive with the connection

Turn on Netcode's connection approval and put `(playerName, ownedMask, appVersion)` in
`NetworkManager.NetworkConfig.ConnectionData` before `StartClient()`. The server's
`ConnectionApprovalCallback` then decides, **before the client is synchronized into a scene**, and
sets `response.Reason` on a refusal.

The payoff is disproportionate: `GameController.HandleClientDisconnect` already surfaces
`NetworkManager.DisconnectReason` in the lobby, so *"You do not own BASH"* reaches the joiner's panel
with no new plumbing. It also removes the current asymmetry where the name arrives after the player
object spawns.

> **Verify, and treat as the one genuinely risky step in this plan.** `ConnectionApproval` is a
> `NetworkConfig` field, and `NetworkConfig` is what the join handshake hashes alongside the
> `ForceSamePrefabs` prefab set. Assume flipping it makes an old build unable to join a new one, with
> the same reasonless refusal documented in [`bugFixes2.md`](bugFixes2.md) §0. Land it in **one**
> commit and reflash both headsets from that build. Prove `response.Reason` actually arrives in
> `DisconnectReason` with a deliberate refusal before writing any UI that depends on it.

### Step 11 — public rooms, via Lobby as a directory

`Assets/Scripts/Lobby/RoomDirectory.cs`. The host's Relay code is the payload; Unity Lobby is only the
noticeboard.

```csharp
// host, immediately after CreateRelay() has a join code
CreateLobbyAsync(roomName, maxPlayers: 12, new CreateLobbyOptions {
    IsPrivate = false,
    Data = {
        ["joinCode"] = new DataObject(Public, code,               DataObject.IndexOptions.S1),
        ["gameKey"]  = new DataObject(Public, module.gameKey,     DataObject.IndexOptions.S2),
        ["build"]    = new DataObject(Public, Application.version,DataObject.IndexOptions.S3),
        ["host"]     = new DataObject(Public, hostName),
        ["players"]  = new DataObject(Public, "1",                DataObject.IndexOptions.N1),
    }});
```

Four things decide whether this works in practice, and all four are about the service's rules rather
than about C#:

1. **Heartbeat or the room vanishes.** A lobby is deleted after roughly 30 s without
   `SendHeartbeatPingAsync`. Run a ~15 s coroutine for as long as the room is open, and
   `DeleteLobbyAsync` when the host leaves. The timeout is a *feature*: a host that crashes cannot
   leave a ghost room in the browser for ever, which is the failure everyone hits when they try to
   clean up only on a graceful exit.
2. **Rate limits, which are per-lobby and tight** (roughly: query 1/s, update 5 per 5 s, create 2 per
   6 s — **verify against the current docs**). So: the browser's Refresh is throttled and disabled
   while a query is in flight, and the player count is written by the **host only**, coalesced to at
   most one `UpdateLobbyAsync` every ~6 s off `OnClientConnected`/`OnClientDisconnect`. The
   alternative — every joiner also joining the Lobby so `AvailableSlots` is free — doubles the
   heartbeat/leave failure surface for one number.
3. **Publish the build, and grey out rooms this build cannot join.** Because of `ForceSamePrefabs`, a
   room hosted by a different build refuses the join with **no reason string** and the joiner just
   sees "Joining room…" for ever. Filtering on `build` in the browser turns this project's single most
   mystifying failure into a greyed-out row that says *Different version*. This is the highest-value
   line in the whole step.
4. **Degrade, do not block.** If Lobby is unreachable, the two public rows go inert with a reason and
   private rooms keep working. Same principle as the anchoring code: *failure is survivable and
   honest*.

Two more rows on the Play panel:

- **New Public Room** → a `ScrollList` of the games **in your library**, then host + publish.
- **Browse Public Rooms** → a `ScrollList` of query results — `host · game · 3/12 · Join` — filtered
  to your library, greyed with a reason when not (`Not in your library`, `Different version`,
  `Full`). Joining reads `joinCode` out of the lobby and calls the *unchanged* join path.

Prerequisite, once: **Lobby must be enabled for this project in the Unity Cloud dashboard**, as Relay
and Vivox already are. It is the same project ID and the same anonymous sign-in `RelayVivox.Start()`
already performs.

**Done when:** two Editor clients (or one Editor + one headset) see each other's public rooms, the
count updates, a host quitting removes the room within ~30 s, and a deliberately mismatched
`Application.version` greys the row instead of hanging the join.

---

## 8. Who may play what

### Step 12 — `PlayerLibrary`, the room union, and server-side enforcement

- `PlayerLibrary : NetworkBehaviour` on `Player.prefab`, one
  `NetworkVariable<ulong> ownedMask`, **server write permission** — matching `playerName`,
  `spawnSlot` and `roomOwner`, all of which are server-written. The value comes from the approval
  payload of Step 10, so the server sets it and no client ever writes it.
- `RoomLibrary.Union()` — OR of every spawned `PlayerLibrary`. Computed **on demand** when the menu
  opens, not per frame; menu opens are rare and a cache here would need invalidating on spawn,
  despawn and change.
- `MenuControl.BuildKeys` filters `catalog.games` by the union. Still names no game.
- **`GameSelector.RequestGameServerRpc` must check the union too.** The menu filter is cosmetic; this
  is the enforcement. It already refuses a key that is not in `GameRoutes` for exactly this reason
  ("Never hand a client-supplied string to LoadScene") — the union check goes on the next line.

**The honest limit, and it belongs in the doc rather than in a comment nobody reads.** There is no
dedicated server here; the "server" is another player's headset, and `ownedMask` is asserted by the
client that sends it. A modified client can claim to own everything. This gate is a **UX and social
rule, not DRM** — the paid game's scene and assets ship inside the APK either way, because Meta
add-ons gate entitlement, not delivery. Say so out loud, decide it is acceptable (it is: the
population that will patch an APK to play a board game with friends who already own it is not a
revenue line), and do not build a defence that cannot work.

### Step 13 — the public-room lock

`RoomAnchor` gains `NetworkVariable<FixedString32Bytes> roomGameKey` and
`NetworkVariable<bool> isPublic`, both server-written, alongside the anchor identity and the content
pose. They are facts about the room, which is what that object is for, and a late joiner then gets
them in the same replication pass as everything else.

- **Public room:** `RequestGameServerRpc` refuses anything but `roomGameKey`; the room menu shows only
  that game, with a line saying why; the approval callback refuses a joiner whose mask lacks that bit,
  with `Reason = "You do not own <label>"`.
- **Private room:** any game in the union, exactly as §8.

Plumbing note: `RoomAnchor` is spawned by `BoardAnchor.HandleServerStarted`, *after* the host has
already chosen public/private on the Play panel. Carry the choice in a small `RoomOptions` static, the
way `GameController.joinCode` / `nickName` already are — and **reset it explicitly**, because
[static state outlives a scene](../CLAUDE.md#conventions-that-break-silently) and a Play session with
domain reload off. `BoardAnchor.Awake` already resets `LocalIsAligned` for precisely this reason;
follow it.

---

## 9. The room menu

### Step 14 — `RoomMenu.prefab` replaces `Menu1` + `Menu2`

Same `Panel` + `ScrollList`. What changes, and what deliberately does not:

**Unchanged, and must stay unchanged:** dispatch by `keyInfo.keyName`; press on trigger down, act on
trigger up; the three room keys in `HandleKey` (`Passthrough`, `Voice Chat`, `Place Anchor`);
`GameRoutes.IsGameKey` → `GameSelector.RequestGame`; `GameSessionRegistry.Active.InvokeMenuAction`;
`GameModule.menuActions`; `BuildRulesPanel`; and the rule that **nothing in `MenuControl` names a
game**.

**What the conversion buys:**

- The game list scrolls, so the `ComfortableColumnKeys = 4` ceiling and its warning go away. That
  warning exists because "past about four games the column pushes the action row into the authored
  `Voice Chat` key at `z −0.631` and then off the panel" — a store with a growing catalog walks
  straight into it.
- **`Menu1`/`Menu2` collapse into one prefab.** The split exists only to hide `Voice Chat` from
  non-hosts; with generated rows that is a `SetActive`. `OpenMenu1`'s prefab-picking branch and its
  "Menu2 is not assigned" fallback both go.
- **`Passthrough` becomes reachable.** `HandleKey` has handled it all along and nothing ever built a
  key for it — a live handler with no way to press it. Generate the row.
- Game rows are filtered by the room library (§8), and in a public room reduced to one with a reason.
- `X` no longer opens the menu in the lobby: gate `ButtonXDown` on
  `GameRoutes.IsGameScene(SceneManager.GetActiveScene().name)`.

`AdoptSceneDefaults` / `ApplyPointerDefault` / `SetKeepPointerAlwaysOn` are untouched — the pointer
contract is orthogonal to how the menu is drawn, and it has already cost this project two bugs.

**Done when:** an eight-entry catalog scrolls in the menu, `Place Anchor` and `Passthrough` both work,
the host sees `Voice Chat: ON/OFF` and a client does not, and BASH's two action keys and Stairs' one
still dispatch through `IGameSession`.

---

## 10. The Meta Platform SDK swap

### Step 15 — `MetaEntitlementService`

1. Add `com.meta.xr.sdk.platform` (same scoped registry, match the Core SDK's `205.0.0`). **Then
   re-run `powershell -File Tools/MetaSdkPatch/Apply-MetaSdkPatch.ps1`** — a package re-resolve
   rewrites `Library/PackageCache`, and the patch is gitignored by construction. See
   [Opening the project for the first time](../CLAUDE.md#opening-the-project-for-the-first-time).
2. Set the App ID in `OVRPlatformSettings`. Add `Oculus.Platform` to `MRBoardGame.Shared.asmdef`.
3. Define `MRBG_META_PLATFORM` for Android only, so the Editor keeps running the mock and the whole of
   §3–§9 stays testable without a headset.
4. Implement the table in §5. `Core.AsyncInitialize()` → `Entitlements.IsUserEntitledToApplication()`;
   a failed entitlement check must quit, which Meta requires of every store title.
5. Create each paid game as a **durable add-on** in the Developer Dashboard and put its SKU in
   `GameModule.metaSku`. `GameCatalogValidator` (§4) already fails the build on a paid module with no
   SKU.
6. `Users.GetLoggedInUser()` supplies `DisplayName`, which is what finally makes Step 8's first source
   real.

Colocation is worth checking at the same time: **verify** whether this project's group-shared
`OVRSpatialAnchor` path needs the Platform SDK initialized on a store build. It works today without
it, so if anything changes when the SDK arrives, it will change here — take a `ColocationProbe`
baseline before and after, per the shared doc's advice.

---

## 11. Order of work

Each step is independently shippable and leaves the app working. The dependency chain is short: 1 → 2
→ 3 gates everything visual; 4 → 6 gates everything commercial; 10 gates 12 → 13.

| # | Step | Depends on | Testable with |
| --- | --- | --- | --- |
| 1 | `keyInfo` renderer-agnostic | — | Editor alone |
| 2 | `Panel` + `PanelRow` prefabs | 1 | Editor alone |
| 3 | `ScrollList` | 2 | Editor alone |
| 4 | Store fields on `GameModule` | — | Editor alone |
| 5 | `GameCatalogValidator` | 4 | Editor alone |
| 6 | `IEntitlementService` + mock | 4 | Editor alone |
| 7 | Library canvas | 3, 6 | Editor alone |
| 8 | Username without the keyboard | 3 | Editor alone |
| 9 | Private create / join; delete `Keyboard` | 8 | **Two clients** |
| 10 | Connection approval carries name + mask | 9 | **Two clients**, both reflashed |
| 11 | Public rooms via `RoomDirectory` | 9 | **Two clients** + Lobby enabled |
| 12 | `PlayerLibrary` + union + server check | 10 | **Two clients** |
| 13 | Public-room lock on `RoomAnchor` | 11, 12 | **Two clients** |
| 14 | `RoomMenu.prefab` | 3, 12 | Editor for layout, two clients for switching |
| 15 | `MetaEntitlementService` | 6 | **Headset**, store build |

Steps 1–8 are entirely Editor work — `InputReader` falls back to the keyboard per hand, so the whole
lobby is playable with `.` for the right trigger and the arrow keys, per
[Build and test](../CLAUDE.md#build-and-test). Steps 9–14 need two running clients. Nothing here needs
two headsets in one room except a colocation regression check after 10 and 15.

**Doc updates, in the same commit as the code**, per the repo's own rule:

| Step | Doc |
| --- | --- |
| 1, 3, 14 | [Menus, pointer, keys](../Assets/Scripts/CLAUDE.md#menus-pointer-keys) — the key model, and `Menu1`/`Menu2` collapsing into one prefab |
| 4, 5, 6 | a new `Assets/Scripts/Store/CLAUDE.md`, plus its row in the root table |
| 9, 10, 11 | [Session flow](../Assets/Scripts/CLAUDE.md#session-flow) — the lobby is no longer a keyboard; approval; the directory |
| 12, 13 | root [Conventions that break silently](../CLAUDE.md#conventions-that-break-silently) — `RoomAnchor`'s two new server-written variables |
| 9 | root — `Keyboard`, `Key.prefab`, `Menu1`, `Menu2`, `Row1`, `GameKeyTemplate`, `ActionKeyTemplate` are all named in the load-bearing-names list and several of them stop existing |
| 4 | root [Adding a game](../CLAUDE.md#adding-a-game) — the contract grows a `productId` and a `libraryBit`, and that list is the one people follow |

Then `powershell -File Tools/Check-DocLinks.ps1`.

---

## 12. What this plan does not do

- **The wrist `SpawnMenu`.** Plateau's, its own dispatcher, not in the way.
- **Any game's rules or scene.** Nothing under `Assets/Scripts/Plateau/`, `Bash/` or `Stairs/` is
  touched by any step.
- **Meta Group Presence, invites, Destinations, rich presence.** These are what let someone join a
  friend from the Quest system UI, and they are the natural sequel to §7 — but they are a separate
  Platform SDK surface and none of what was asked for needs them.
- **Cloud-saved libraries or cross-buy.** The library is local (mock) or the platform's (real).
- **Matchmaking.** "Browse public rooms" is a query, not a matchmaker.
- **A ray-driven uGUI input module.** See §0. If a later feature genuinely needs `Button`,
  `InputField` focus or `Dropdown`, that is the moment to write one — not before.

---

## 13. Store readiness outside this plan

Cheap items that a store submission will ask for and that are visible in the repo today:

- **`ColocationProbe` is `m_Enabled: 1` on `PersistentRig.prefab`, i.e. running in every build**, at
  1 Hz into the debug box. Its own class comment says to delete it once the numbers are known. Ship
  with it off.
- **`GameScene` is still at build index 3** and is unreachable — it is in no `GameModule`. Remove it
  from `EditorBuildSettings` and delete the scene; it is dead weight in the APK. Note that this
  renumbers the build list, which nothing here reads by index.
- **`BoardAnchor.dll` is a tracked binary at the repo root** and `build/`, `BoardGames.apk`, three
  `.sln` files and two `*_BurstDebugInformation_DoNotShip/` folders are root clutter.
- **`Assets/Resources/BillingMode.json`** is a Unity IAP leftover from the previous application and
  should go when §5 lands, so nobody later mistakes it for this project's billing config.
- Data Use Checkup declarations, age rating, privacy policy URL, store art, and a real
  `Application.version` / `bundleVersionCode` policy — none of which are code, all of which block
  submission.

---

## 14. How it came out

Steps 1–14 are applied. Everything below is a difference from the plan above, so the plan can be
read as written and this read as the correction.

### What is different from the plan

**One `Panel` prefab, and `RoomMenu.prefab` is a variant of it.** The plan had `Panel` + `PanelRow`
as separate prefabs and `RoomMenu` as a third. `PanelRow` turned out not to want to be a prefab at
all — it is only ever cloned from an inactive template *inside* the list that owns it, which is
`MenuControl`'s existing idiom and keeps the row's width in step with its viewport by construction.
So there are three prefabs, all under `Assets/Prefabs/Ui/`: `Panel.prefab`, `RoomMenu.prefab` (a
**variant**, so a fix to the shared row layout reaches both and a re-skin of the menu does not touch
the lobby) and `CodePad.prefab`.

**`Panel` owns the press model, not each caller.** The plan left press-down/act-up in `MenuControl`
and implied the lobby would repeat it. It is on `Panel`, with an `IsMine` check so the lobby's two
simultaneously-open panels can share one pointer without a press on one firing on the other.

**A `Panel` carries two `ScrollList`s, not one.** `Main` scrolls; `Actions` does not. Putting the
room's actions in the scrolling list would let a fourth game push `Place Anchor` off the bottom —
which is precisely the failure the old fixed column had, re-created in a new layout system.

**The world-space Canvas facing needed a 180° flip that the plan does not mention**, and it bit
twice: a Canvas draws on its `+Z` face, so a panel given the camera's own rotation shows its back.
`MenuControl` therefore takes its 0.7 m left offset off the **camera's** `right` rather than the
menu's, and `BuildRulesPanel` places the rules wing in **world** space and parents it with
`worldPositionStays` — menu-local coordinates now run backwards on two axes *and* are in
millimetres, since the menu root is itself a Canvas at scale 0.001.

**`IEntitlementService` grew `IsOwned(string)`.** `Owned.Contains(...)` on an
`IReadOnlyCollection<string>` binds to the `ReadOnlySpan<char>` extension and does not compile.

**`RoomDirectory` and `RoomApproval` live on the Network Manager**, not in the lobby scene: the
directory's heartbeat has to survive the host leaving the lobby for a game, and Netcode marks that
object `DontDestroyOnLoad`. The plan did not say where they went.

**Step 8's username has no dedicated source order yet.** It is `IEntitlementService.DisplayName`
(the mock's remembered `PlayerPrefs` name, or `"Player"`) or typed on the pad. The Meta name half
arrives with step 15.

**`Text Input` went with `Keyboard`.** The plan only names `Keyboard`, but the `Text Input` canvas
was the room-code display that keyboard fed; the panel's status line replaced both.

### The **verify** items

| Item | Result |
| --- | --- |
| §3 widening `keyInfo.keyLabel` to `TMP_Text` keeps existing assignments | **Confirmed.** `Menu1`/`Menu2` are out of the wiring now, but `SpawnMenu`'s keys still hold their labels. |
| §7 step 9 — the Relay join-code alphabet | **Not measured.** The pad types all of `A-Z 0-9`, which is the safe direction to be wrong in: a spare key that never appears in a code is invisible, whereas a missing one makes a room unjoinable with nothing to say so. Still worth generating a handful of codes and looking. |
| §7 step 10 — `response.Reason` actually reaching `DisconnectReason` | **Not tested.** Needs two clients. This is the one genuinely risky step and it has not been proven end to end. |
| §7 step 11 — Lobby rate limits against current docs | **Not checked.** The numbers in `RoomDirectory` are the plan's, as serialized fields. |

### What has never been run

Everything that needs a second client, which is steps 9–14's real acceptance:

- Two clients seeing each other's public rooms, the count updating, a host quitting removing the
  room within ~30 s, a mismatched `Application.version` greying a row.
- Connection approval refusing anything, with a reason the joiner can read.
- `PlayerLibrary` replicating, `RoomLibrary.Union()` across two players, the public-room lock.
- **Lobby has not been enabled in the Unity Cloud dashboard.** Until it is, every public-room call
  fails and the two public rows go inert with a reason — which is the designed degradation, so the
  lobby still works, but nothing about §7 step 11 has been exercised.

What *has* been verified, in the Editor: the whole project compiles across all five assemblies; the
lobby comes up with both panels placed and facing the player, the Library listing the three catalog
games and the Play panel's five rows in their correct states (`New Public Room` greyed with
`Library empty` on a first run); the code pad generates 36 characters plus its four controls; and
`RoomMenu.prefab` opens with the three game rows, `Place Anchor`, `Passthrough` and the rules wing
at the right scale and facing.

### Rough edges left in

- **The rules wing and the game list both read `rightJoystick.y`.** With more than five games in the
  catalog, a joystick push scrolls both. Neither is destructive. Fix by giving `ScrollList` a
  modifier, or by moving the rules panel onto a `ScrollList` of its own.
- **`OpenMenu1` called twice without a `CloseMenu` leaks the first menu.** Pre-existing; only
  reachable from code, since `X` toggles.
- **§13's cheap store-readiness items are untouched**, except that `GameCatalogValidator` now exists.
  `ColocationProbe` still ships enabled, `GameScene` is still at build index 3, `BoardAnchor.dll` is
  still a tracked binary at the repo root, and `BillingMode.json` is still there.
