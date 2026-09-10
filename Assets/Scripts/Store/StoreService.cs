using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// The whole app's view of who owns what: a static façade over one <see cref="IEntitlementService"/>,
/// plus the ulong library mask that goes on the wire.
///
/// Static for the same reason GameRoutes is: the things that ask — the lobby panels, the room menu,
/// the connection approval callback — have no Inspector slot a scene could fill, and two of them
/// run before any player object exists.
///
/// The mask is the only form the library travels in. It is composed here, from
/// GameModule.libraryBit, so the mapping between a saved productId and a wire bit exists in exactly
/// one place and both halves are authored on the same asset.
/// </summary>
public static class StoreService
{
    private static IEntitlementService s_service;
    private static bool s_initializing;

    /// <summary>Raised when the library or a price changed. Panels redraw off this.</summary>
    public static event Action Changed;

    /// <summary>True once InitializeAsync has come back. False means "do not trust Owns yet".</summary>
    public static bool Ready { get; private set; }

    /// <summary>
    /// The live service. Creating the mock on first touch is deliberate — nothing in a scene owns
    /// the store, and the Editor must keep running the mock even after the Meta package lands
    /// (MetaEntitlementService is Android-only, behind MRBG_META_PLATFORM).
    /// </summary>
    public static IEntitlementService Service
    {
        get
        {
            if (s_service == null)
            {
                Use(CreateDefault());
            }
            return s_service;
        }
    }

    private static IEntitlementService CreateDefault()
    {
#if MRBG_META_PLATFORM
        return new MetaEntitlementService();
#else
        return new MockEntitlementService();
#endif
    }

    /// <summary>Swap the implementation. Tests and the §10 swap are the only callers.</summary>
    public static void Use(IEntitlementService service)
    {
        if (s_service != null)
        {
            s_service.Changed -= Raise;
        }

        s_service = service;
        Ready = false;

        if (s_service != null)
        {
            s_service.Changed += Raise;
        }
    }

    /// <summary>
    /// Bring the library up. Safe to call more than once and from more than one place — the lobby
    /// calls it on load and the approval payload needs it before StartClient.
    /// </summary>
    public static async Task InitializeAsync()
    {
        if (Ready || s_initializing)
        {
            return;
        }

        s_initializing = true;
        try
        {
            Ready = await Service.InitializeAsync();
            await RefreshPricesAsync();
        }
        catch (Exception e)
        {
            // A store that will not come up must not take the lobby down with it: private rooms and
            // an empty library still work. Same principle as the anchoring code — failure is
            // survivable and honest.
            Debug.LogError("StoreService: the entitlement service failed to initialize. The " +
                           "library will read as empty. " + e);
            Ready = false;
        }
        finally
        {
            s_initializing = false;
            Raise();
        }
    }

    /// <summary>Fetch the platform's formatted price for every paid game in the catalog.</summary>
    public static async Task RefreshPricesAsync()
    {
        GameCatalog catalog = GameCatalog.Instance;
        if (catalog == null)
        {
            return;
        }

        List<string> skus = new List<string>();
        for (int i = 0; i < catalog.games.Count; i++)
        {
            GameModule module = catalog.games[i];
            if (module != null && module.isPaid && !string.IsNullOrEmpty(module.metaSku))
            {
                skus.Add(module.metaSku);
            }
        }

        if (skus.Count > 0)
        {
            await Service.RefreshPricesAsync(skus);
        }
    }

    public static bool Owns(GameModule module)
    {
        return module != null && Owns(module.productId);
    }

    public static bool Owns(string productId)
    {
        return Service.IsOwned(productId);
    }

    /// <summary>
    /// This player's library as it travels: one bit per owned game, from GameModule.libraryBit.
    /// A module with no valid bit contributes nothing rather than corrupting the mask.
    /// </summary>
    public static ulong OwnedMask
    {
        get
        {
            GameCatalog catalog = GameCatalog.Instance;
            if (catalog == null)
            {
                return 0UL;
            }

            ulong mask = 0UL;
            for (int i = 0; i < catalog.games.Count; i++)
            {
                GameModule module = catalog.games[i];
                if (module != null && Owns(module))
                {
                    mask |= module.LibraryMask;
                }
            }
            return mask;
        }
    }

    /// <summary>Is this game in the given mask? The one place that reads a bit out of one.</summary>
    public static bool MaskAllows(ulong mask, GameModule module)
    {
        if (module == null)
        {
            return false;
        }

        ulong bit = module.LibraryMask;
        return bit != 0UL && (mask & bit) != 0UL;
    }

    public static bool MaskAllows(ulong mask, string gameKey)
    {
        GameCatalog catalog = GameCatalog.Instance;
        return catalog != null && MaskAllows(mask, catalog.ByKey(gameKey));
    }

    /// <summary>
    /// Add a free game. Free products are explicitly added rather than auto-granted, which is why
    /// the Play panel must never be a dead end: "Join Private Room" works with an empty library and
    /// a disabled row always says why.
    /// </summary>
    public static async Task<bool> AddFreeAsync(GameModule module)
    {
        if (module == null || string.IsNullOrEmpty(module.productId))
        {
            return false;
        }

        bool ok = await Service.GrantFreeAsync(module.productId);
        Raise();
        return ok;
    }

    public static async Task<PurchaseResult> PurchaseAsync(GameModule module)
    {
        if (module == null || string.IsNullOrEmpty(module.productId))
        {
            return PurchaseResult.Failed("no product");
        }

        PurchaseResult result = await Service.PurchaseAsync(module.productId, module.metaSku);
        Raise();
        return result;
    }

    /// <summary>The store's price string for a game, or null. Never composed locally.</summary>
    public static string PriceFor(GameModule module)
    {
        return module != null ? Service.PriceFor(module.metaSku) : null;
    }

    public static string DisplayName => Service.DisplayName;

    public static void RememberDisplayName(string name)
    {
        Service.RememberDisplayName(name);
    }

    private static void Raise()
    {
        Changed?.Invoke();
    }
}
