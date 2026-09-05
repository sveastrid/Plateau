# quest_networking_plan — the Quest 3 → Quest 3S join failure

Working document in the style of the rest of `docs/`: the plan with the evidence attached, so a
claim can be checked rather than taken on trust.

> **Status: instrumentation and the one verified defect are APPLIED. The cause is not yet
> confirmed** — that needs one session in two headsets, and §4 is the experiment to run in it.

## The symptom

Hosting on the Quest 3 and joining from the Quest 3S: the joiner reached the game scene but saw no
other player, did not follow the host's scene changes, and could not change scenes itself. Both
headsets were flashed from the same APK.

Reaching the game scene matters. Netcode only synchronizes a client into the host's scene *after*
the connection is approved, so the config hash matched and the handshake completed. The failure is
a connection that **died during or just after synchronization**: the remote player despawns, later
scene events never arrive, and `GameSelector.RequestGame` early-outs on `!IsOwner` because the
client's own player object never finished spawning.

## What this document used to say, and why it was wrong

The original version proposed one change — `ToRelayServerData("dtls")` → `"udp"` at
`RelayVivox.cs:53` and `:85` — on the grounds that DTLS "frequently fails the handshake between two
Quest devices", evidenced by "hosting on the Unity Editor and joining with the Quest 3 worked fine".
Two of those facts do not survive contact with this repo:

| Claim | What the repo says |
| --- | --- |
| DTLS fails between two Quests | `bugFixes2.md` records a **Quest 3 + Quest 3S session on 2026-09-05 that connected** over this exact code. Its four bugs — alignment, nametag offsets, spawn-menu deselect, BASH seats — all require a working session. Quest↔Quest over DTLS demonstrably works here. |
| Editor host + Quest 3 client worked | `bigFixes1.md` §1, marked **Verified**, says Editor↔device could never connect: `DefaultNetworkPrefabs.asset` held four Meta SDK prefabs under a package `Editor/` folder, stripped from the Android build, and `ForceSamePrefabs: 1` makes that a hard refusal. The control experiment was the one case that was structurally impossible. |
| "Windows handles the DTLS handshake differently" | Not a mechanism. Both ends run the same UTP/DTLS implementation. |

The one Unity forum thread matching this symptom is a host/client connection-type *mismatch*; both
sides here already said `"dtls"`. A second thread that investigated udp-vs-dtls on Android found the
real cause was divergent client/server code, not the protocol.

`"udp"` is valid on Android (`AllocationUtils.GetValidProtocols()` returns UDP/DTLS/WSS on
everything but WebGL), so the change would not have thrown. But if the Relay project ever enforces
secure connections, `AllocationUtils.GetEndpoint` throws `ArgumentException` — **not** a
`RelayServiceException` — which `GameController` did not catch, so it would have escaped the
`async void` and hung the lobby with no message at all. That is now caught.

## Candidate causes, ranked

1. **A double-join racing the first one.** *Verified code defect, now fixed.* `GameController` set
   `pressedKey` only while the pointer was over a key and never cleared it after acting
   (`MenuControl.cs:174` does; `GameController` did not). The join path deliberately does not
   `LoadScene`, so between `StartClient()` and Netcode pulling the client into the host's scene the
   joiner was still in `OpeningScene` with a live `GameController` and `pressedKey` still on
   `Enter`. One more press-and-release anywhere re-ran the whole join: a second
   `JoinAllocationAsync` whose fresh allocation **invalidates the first**, a second
   `SetRelayServerData` mid-connection, and a second `StartClient()` on a running instance. Exactly
   "joined, then nothing", joiner-only, and the window is longest on the slower headset.
2. **A transport-level drop** — the original theory. Real, but DTLS is only one possible cause;
   host-side send-queue pressure (`m_MaxPacketQueueSize: 128` on a Quest host, against an Editor
   host with far more headroom) fits "the Quest host is the new variable" at least as well.
3. **Colocation misread as a network failure.** Excluded by "would not follow scene changes", but
   `bugFixes2.md` §1 describes two unaligned players seeing each other on opposite sides of the
   board, which looks a lot like "nobody there".

Nothing in the project could tell these apart. `StartHost`/`StartClient` return values were
discarded and nothing subscribed to `OnClientConnectedCallback`, `OnClientStopped` or
`OnTransportFailure` or read `NetworkManager.DisconnectReason` — the only
`OnClientDisconnectCallback` was `RoomAnchor.cs:74`, server-side, clearing the world-grab lock. So
the original plan could have been applied, failed, and taught nothing.

## Applied

### 1. The double-join — `GameController.cs`

`pressedKey` is cleared after acting, the way `MenuControl` does. `HostNewRoom` and
`TryToJoinRelayVivox` also take a `connectInFlight` latch set before the first `await` and cleared
on every failure path, because both are `async void`.

### 2. A failed or lost join is now visible — `GameController.cs`

`bigFixes1.md` §1 Step 3, which was never applied:

- The body of the `catch (RelayServiceException)` block is lifted into `ShowJoinFailed(string why)`,
  so every failure path agrees about what "back to the keyboard" means. It is idempotent — the
  instructions transform is nudged by a *relative* 7 cm, so undoing it twice would walk the text
  off; `joinedRelay` gates that — and it calls `NetworkManager.Shutdown()` so a retry is not
  refused by an instance still grinding through its 60 connect attempts.
- `OnClientDisconnectCallback` and `OnTransportFailure` are subscribed in `Start()` (not `OnEnable`
  — `NetworkManager.Awake` must have run) and dropped in `OnDestroy()`. An empty `DisconnectReason`
  is reported as "The room refused the connection (different build?)", because Netcode's own
  config-hash refusal sends no reason string.
- A 15 s watchdog coroutine (`JoinTimeoutSeconds`) covers the rest: a transport death may never
  raise the disconnect callback, and UnityTransport otherwise retries silently for a full minute
  (`m_MaxConnectAttempts: 60` × `m_ConnectTimeoutMS: 1000`).

### 3. `NetworkProbe` — new, on `Network Manager` in `OpeningScene`

Modelled on `ColocationProbe` and read the same way, through the in-headset `DebugLog` box. It lives
on `Network Manager` rather than the rig because Netcode marks that object `DontDestroyOnLoad`, so
it outlives every `LoadSceneMode.Single` switch and is **still listening when the late drop
happens** — which `GameController` cannot be, since it dies with `OpeningScene` at the moment the
interesting failure starts.

Connection events are always logged; the periodic state line is off by default (`LogHeartbeat`),
because the box holds ten lines and a 1 Hz heartbeat scrolls the events out of it. The state line
carries `prefabs=`, the integer `bigFixes1.md` §1 reduces the whole `ForceSamePrefabs` problem to.

### 4. The connection type is now a field — `RelayVivox.connectionType`

Default `"dtls"`, serialized on `Network Manager`, used by both `CreateRelay` and `JoinRelay` and
logged on each. Both protocols can now be tried in one session without a rebuild. **It must match
on the host and the joiner.** `Start()`'s Unity Services sign-in also got a try/catch and a
`servicesReady` flag — a headset that came up without a network used to sign in silently-never.

### 5. The prefab list — `bigFixes1.md` §1 Steps 1, 2, 4

`DefaultNetworkPrefabs.asset` is pruned to the four project prefabs (`Player`, `Room Anchor`,
`NetworkCannonLine`, `Base`). Nothing in this project spawns the eight Meta Building Blocks prefabs
that were in it — colocation here is hand-rolled on `BoardAnchor`, and their GUIDs appear nowhere
else under `Assets/`. `Assets/Editor/NetworkPrefabListGuard.cs` turns Netcode's auto-generator off
so a package re-resolve does not re-add them, and **fails the build** if a package or `Editor/`
prefab ever gets back in. That build guard is the part that actually protects; the setting can
revert, a `BuildFailedException` cannot.

> `ProjectSettings/NetcodeForGameObjects.asset` is written the first time the Editor loads with the
> guard present. **Commit it** — it is not gitignored, and without it the next clone is back to a
> list that re-adds package prefabs.

## Still to do — the measurement

**Changing the prefab list changed the NetworkConfig hash. Flash both headsets from the same build.**

1. **Editor↔Editor**, then **Editor host ↔ Quest 3 client** — the second was impossible before §5.
   `NetworkProbe`'s `prefabs=` must be equal on both ends.
2. **Reproduce with instrumentation on.** Both headsets on one build, Quest 3 hosts, Quest 3S joins.
   Read the in-headset box on the joiner:
   - a `ShowJoinFailed` message, or `NET: connection LOST` / `NET: peer LEFT` → a drop. Go to 3.
   - `NET: transport FAILURE` → the failure is under Netcode, in UTP/Relay/DTLS. This is the *only*
     reading that justifies the udp experiment.
   - a second `Joining Relay with …` line → it was the double-join, and §1 fixed it.
   - `connected=True`, `clients=2`, scene changes *do* follow → never a network fault. Go to
     `ColocationProbe` and `bugFixes2.md` §1.
3. **The A/B.** Only if 2 says "transport drop": set `connectionType` to `udp` **on both headsets**
   and repeat. Record the result here — the next person should inherit a measurement, not a guess.
