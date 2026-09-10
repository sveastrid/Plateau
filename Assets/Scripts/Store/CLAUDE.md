# The store — `Assets/Scripts/Store/`

Who owns which games. A mock today, the Meta Platform SDK when a paid game ships, and one interface
between them that the rest of the project talks to.

Read the root [`CLAUDE.md`](../../../CLAUDE.md) and
[the shared systems doc](../CLAUDE.md) first — especially
[Rooms, libraries and the store](../CLAUDE.md#rooms-libraries-and-the-store), which is the half of
this that touches the network.

This is a subfolder of `MRBoardGame.Shared` with no `.asmdef` of its own, exactly as
`Assets/Scripts/Games/` is.

## The one rule

**Nothing outside this folder may name `Oculus.Platform` or `PlayerPrefs`.** Everything else asks
`StoreService`. That is the same discipline `IGameSession` enforces between shared code and the
games, and it exists for the same reason: it is what makes §10 of
[`LobbyUpdate.md`](../../../docs/LobbyUpdate.md) a constructor change rather than a search across
the project.

If you find yourself wanting `PlayerPrefs` in a panel, the thing you want is a method on
`IEntitlementService`.

## The files

| File | What it is |
| --- | --- |
| `IEntitlementService.cs` | The seam, plus `PurchaseOutcome` / `PurchaseResult`. |
| `MockEntitlementService.cs` | `PlayerPrefs`, keyed `MRBG.owned.<productId>` = `"1"`. |
| `MockStoreSettings.cs` | `Resources/MockStoreSettings.asset` — latency, **force-failure**, **force-cancel**, clear-library. |
| `StoreService.cs` | Static façade. Owns the `ulong` mask, composes it from `GameModule.libraryBit`, raises `Changed`. |
| `MetaEntitlementService.cs` | The real one, entirely inside `#if MRBG_META_PLATFORM`. **Not compiled today** — see below. |

## Why the interface is shaped the way it is

Method for method against Meta's IAP surface, so the swap is mechanical:

| `IEntitlementService` | Mock | Meta Platform SDK |
| --- | --- | --- |
| `InitializeAsync` | read `PlayerPrefs` | `Core.AsyncInitialize` → `Entitlements.IsUserEntitledToApplication` → `IAP.GetViewerPurchases` |
| `RefreshPricesAsync(skus)` | copy `mockPriceLabel` | `IAP.GetProductsBySKU` — **the formatted, localized price** |
| `PurchaseAsync` | fake latency, then write | `IAP.LaunchCheckoutFlow` |
| `GrantFreeAsync` | write | write locally; free products have no SKU |
| `RefreshOwnedAsync` | re-read | `IAP.GetViewerPurchases` |
| `DisplayName` | last-used name / `"Player"` | `Users.GetLoggedInUser` |

`IsOwned(string)` is a method rather than `Owned.Contains(...)` because `Contains` on an
`IReadOnlyCollection<string>` binds to the `ReadOnlySpan<char>` extension and does not compile.

**`PurchaseResult` distinguishes four outcomes and that is not over-engineering.** Meta returns all
four, and a UI that collapses them shows *"purchase failed"* to somebody who pressed Back. The
lobby's `Cancelled` branch deliberately clears the status line and says nothing.

**Prices are a store-compliance point, not a display detail.** Meta returns a price formatted in the
viewer's currency; a hardcoded `"$4.99"` is wrong for most of the planet and review catches it.
`GameModule.mockPriceLabel` exists so the mock can render *something*, and `MetaEntitlementService`
must overwrite it — never fall back to it. A paid row whose price has not arrived shows `...` and is
not pressable, rather than showing a placeholder that looks like a price.

## Two identities per game, and neither is the catalog index

- **`productId`** (`"mrbg.stairs"`) keys the saved library, which outlives app updates.
- **`libraryBit`** (0..63) is what travels: a `ulong` mask is 8 bytes against a `NetworkList` of
  strings, and it rides in the connection approval payload before any object has spawned.

Both are authored on the same asset, so the mapping lives in one place. Neither may be the catalog
index: reordering `GameCatalog.games` between two app versions would silently hand a player a
different game than the one they bought. **Both are stable forever and are never reused.**
`GameCatalogValidator` fails the build on a duplicate, an empty `productId`, or a bit outside 0..63.

Current assignment — do not renumber:

| Module | `productId` | `libraryBit` |
| --- | --- | --- |
| `StairsModule` | `mrbg.stairs` | 0 |
| `PlateauModule` | `mrbg.chasms` | 1 |
| `BashModule` | `mrbg.bash` | 2 |

All three are **free**, per what was asked for. Nothing in the project is paid yet, so the purchase
path has never run against a real SKU — give one module a fake paid twin (a fourth catalog row
pointing at an existing scene) to exercise it, and delete it before ship.

## Free games are added, not granted

`StoreService` does not seed the catalog's free games on first run. That is a deliberate fork, and
it puts an obligation on the UI: **the Play panel must never be a dead end.** "Join Private Room"
works with an empty library, and "New Public Room" is greyed with `Library empty` rather than being
pressable and then failing. If the empty first run reads badly in a headset, seeding every free
product is two lines in `MockEntitlementService.InitializeAsync`.

## Turning the real store on

Not done, and deliberately last. `com.meta.xr.sdk.platform` is **not** in `Packages/manifest.json`
and `Oculus.Platform` does not resolve, so `MetaEntitlementService.cs` is inside `#if
MRBG_META_PLATFORM` and **has never been compiled**. Treat it as a careful draft, not as tested code.

1. Add `com.meta.xr.sdk.platform` (scoped registry `npm.developer.oculus.com`, matching the Core
   SDK's `205.0.0`). **Then re-run `Tools/MetaSdkPatch/Apply-MetaSdkPatch.ps1`** — a package
   re-resolve rewrites `Library/PackageCache` and the patch is gitignored by construction. See
   [Opening the project for the first time](../../../CLAUDE.md#opening-the-project-for-the-first-time).
2. Set the App ID in `OVRPlatformSettings`; add `Oculus.Platform` to `MRBoardGame.Shared.asmdef`.
3. Define `MRBG_META_PLATFORM` **for Android only**, so the Editor keeps running the mock and the
   whole lobby stays testable without a headset.
4. Create each paid game as a **durable add-on** in the Developer Dashboard and put its SKU in
   `GameModule.metaSku`. `GameCatalogValidator` already fails the build on a paid module with no SKU.
5. `Assets/Resources/BillingMode.json` is a Unity IAP leftover from *Math Classroom* and is not this
   project's billing config. Delete it when this lands, before somebody believes it.

Two things to check at the same time, because both are the sort that only fail on a store build:

- **The callback pump.** Every Platform SDK request completes out of `Request.RunCallbacks()`, which
  nothing in this project would otherwise call. `MetaPlatformPump` is a `DontDestroyOnLoad`
  `MonoBehaviour` created in `InitializeAsync` that does. Without it every `await` hangs for ever
  with no error.
- **Colocation.** Take a `ColocationProbe` baseline before and after: whether this project's
  group-shared `OVRSpatialAnchor` path needs the Platform SDK initialized on a store build is
  unverified. It works today without it, so if anything changes it will change here.
