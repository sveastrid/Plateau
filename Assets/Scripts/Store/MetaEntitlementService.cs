#if MRBG_META_PLATFORM
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using Oculus.Platform;
using Oculus.Platform.Models;

/// <summary>
/// The real store. Compiled only when MRBG_META_PLATFORM is defined, which is Android-only on
/// purpose: the Editor keeps running MockEntitlementService, so the whole of the lobby, the library
/// panel and the room library stay testable without a headset.
///
/// Turning this on is four things, in this order, and the first one bites:
///
///  1. Add com.meta.xr.sdk.platform (same scoped registry, matching the Core SDK's 205.0.0), then
///     **re-run Tools/MetaSdkPatch/Apply-MetaSdkPatch.ps1** — a package re-resolve rewrites
///     Library/PackageCache and the patch is gitignored by construction.
///  2. Set the App ID in OVRPlatformSettings, and add Oculus.Platform to MRBoardGame.Shared.asmdef.
///  3. Define MRBG_META_PLATFORM for Android in Player Settings.
///  4. Create each paid game as a durable add-on in the Developer Dashboard and put its SKU on the
///     GameModule. GameCatalogValidator already fails the build on a paid module with no SKU.
///
/// A failed entitlement check must quit. Meta requires that of every store title.
///
/// This and MockEntitlementService are the only two files allowed to name Oculus.Platform.
/// </summary>
public class MetaEntitlementService : IEntitlementService
{
    private readonly HashSet<string> ownedSkus = new HashSet<string>();
    private readonly HashSet<string> owned = new HashSet<string>();
    private readonly Dictionary<string, string> prices = new Dictionary<string, string>();

    public event Action Changed;

    public IReadOnlyCollection<string> Owned => owned;

    public bool IsOwned(string productId) =>
        !string.IsNullOrEmpty(productId) && owned.Contains(productId);

    public string DisplayName { get; private set; } = "Player";

    public async Task<bool> InitializeAsync()
    {
        MetaPlatformPump.Ensure();

        try
        {
            if (!Core.IsInitialized())
            {
                await Await<PlatformInitialize>(Core.AsyncInitialize());
            }

            EntitlementCheckResult entitled = await Await<EntitlementCheckResult>(
                Entitlements.IsUserEntitledToApplication());

            if (entitled == null)
            {
                // Meta requires an unentitled copy to stop. Quitting is the whole response: there
                // is no degraded mode to fall back to on a store build.
                Debug.LogError("MetaEntitlementService: this copy is not entitled. Quitting.");
                Application.Quit();
                return false;
            }
        }
        catch (Exception e)
        {
            Debug.LogError("MetaEntitlementService: platform initialization failed. " + e);
            return false;
        }

        try
        {
            User user = await Await<User>(Users.GetLoggedInUser());
            if (user != null && !string.IsNullOrEmpty(user.DisplayName))
            {
                DisplayName = user.DisplayName;
            }
            else if (user != null && !string.IsNullOrEmpty(user.OculusID))
            {
                DisplayName = user.OculusID;
            }
        }
        catch (Exception e)
        {
            // A name is a nicety; not having one must not stop the store working.
            Debug.LogWarning("MetaEntitlementService: could not read the logged-in user. " + e);
        }

        await RefreshOwnedAsync();
        return true;
    }

    public async Task RefreshOwnedAsync()
    {
        try
        {
            PurchaseList purchases = await Await<PurchaseList>(IAP.GetViewerPurchases());

            ownedSkus.Clear();
            if (purchases != null)
            {
                foreach (Purchase purchase in purchases)
                {
                    if (!string.IsNullOrEmpty(purchase.Sku))
                    {
                        ownedSkus.Add(purchase.Sku);
                    }
                }
            }

            RebuildOwned();
        }
        catch (Exception e)
        {
            Debug.LogError("MetaEntitlementService: could not read viewer purchases. " + e);
        }
    }

    public async Task RefreshPricesAsync(IEnumerable<string> skus)
    {
        if (skus == null)
        {
            return;
        }

        List<string> list = new List<string>(skus);
        if (list.Count == 0)
        {
            return;
        }

        try
        {
            ProductList products = await Await<ProductList>(IAP.GetProductsBySKU(list.ToArray()));
            if (products != null)
            {
                foreach (Product product in products)
                {
                    // FormattedPrice is the localized string. Never compose one — a hardcoded
                    // "$4.99" is wrong for most of the planet and store review catches it.
                    prices[product.Sku] = product.FormattedPrice;
                }
            }
            Raise();
        }
        catch (Exception e)
        {
            Debug.LogError("MetaEntitlementService: could not fetch prices. " + e);
        }
    }

    public string PriceFor(string sku)
    {
        if (string.IsNullOrEmpty(sku))
        {
            return null;
        }
        return prices.TryGetValue(sku, out string price) ? price : null;
    }

    public async Task<PurchaseResult> PurchaseAsync(string productId, string sku)
    {
        if (string.IsNullOrEmpty(sku))
        {
            return PurchaseResult.Failed("no SKU on this product");
        }

        if (ownedSkus.Contains(sku))
        {
            return PurchaseResult.AlreadyOwned();
        }

        try
        {
            Message<Purchase> message = await AwaitMessage<Purchase>(IAP.LaunchCheckoutFlow(sku));

            if (message.IsError)
            {
                Error error = message.GetError();

                // Meta reports a player backing out of checkout as an error. Showing that as
                // "purchase failed" to somebody who pressed Back is exactly what the four-way
                // PurchaseOutcome exists to prevent.
                if (IsCancellation(error))
                {
                    return PurchaseResult.Cancelled();
                }

                return PurchaseResult.Failed(error != null ? error.Message : "checkout failed");
            }

            ownedSkus.Add(sku);
            RebuildOwned();
            return PurchaseResult.Succeeded();
        }
        catch (Exception e)
        {
            return PurchaseResult.Failed(e.Message);
        }
    }

    /// <summary>
    /// Free products have no SKU and nothing to buy, so the platform has nothing to say about them.
    /// They are added locally, exactly as in the mock — cross-device carry-over of a free add is
    /// out of scope, per §12 of docs/LobbyUpdate.md.
    /// </summary>
    public Task<bool> GrantFreeAsync(string productId)
    {
        if (string.IsNullOrEmpty(productId))
        {
            return Task.FromResult(false);
        }

        owned.Add(productId);
        PlayerPrefs.SetString("MRBG.owned." + productId, "1");
        PlayerPrefs.Save();
        Raise();
        return Task.FromResult(true);
    }

    public void RememberDisplayName(string name)
    {
        // The platform owns the name here. A typed one is still remembered so a player who edits it
        // gets their edit back on the next launch.
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        DisplayName = name;
        PlayerPrefs.SetString("MRBG.displayName", name);
        PlayerPrefs.Save();
        Raise();
    }

    /// <summary>
    /// SKUs are the platform's identity for a product; productIds are this project's. The catalog
    /// carries both on the same asset, which is what makes the translation a lookup rather than a
    /// second source of truth.
    /// </summary>
    private void RebuildOwned()
    {
        owned.Clear();

        GameCatalog catalog = GameCatalog.Instance;
        if (catalog != null)
        {
            for (int i = 0; i < catalog.games.Count; i++)
            {
                GameModule module = catalog.games[i];
                if (module == null || string.IsNullOrEmpty(module.productId))
                {
                    continue;
                }

                bool bought = !string.IsNullOrEmpty(module.metaSku) && ownedSkus.Contains(module.metaSku);
                bool added = !module.isPaid &&
                             PlayerPrefs.GetString("MRBG.owned." + module.productId, "") == "1";

                if (bought || added)
                {
                    owned.Add(module.productId);
                }
            }
        }

        Raise();
    }

    private static bool IsCancellation(Error error)
    {
        if (error == null)
        {
            return false;
        }

        string message = error.Message ?? "";
        return message.IndexOf("cancel", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static Task<T> Await<T>(Request<T> request)
    {
        TaskCompletionSource<T> completion = new TaskCompletionSource<T>();
        request.OnComplete(message =>
        {
            if (message.IsError)
            {
                Error error = message.GetError();
                completion.SetException(new Exception(error != null ? error.Message : "platform error"));
                return;
            }
            completion.SetResult(message.Data);
        });
        return completion.Task;
    }

    private static Task<Message<T>> AwaitMessage<T>(Request<T> request)
    {
        TaskCompletionSource<Message<T>> completion = new TaskCompletionSource<Message<T>>();
        request.OnComplete(message => completion.SetResult(message));
        return completion.Task;
    }

    private void Raise()
    {
        Changed?.Invoke();
    }
}

/// <summary>
/// The Platform SDK delivers every callback from Request.RunCallbacks(), which somebody has to call
/// once a frame. Nothing in this project otherwise would, and without it every await above hangs
/// for ever with no error.
/// </summary>
internal class MetaPlatformPump : MonoBehaviour
{
    private static MetaPlatformPump s_instance;

    public static void Ensure()
    {
        if (s_instance != null)
        {
            return;
        }

        GameObject go = new GameObject("Meta Platform Pump");
        DontDestroyOnLoad(go);
        s_instance = go.AddComponent<MetaPlatformPump>();
    }

    void Update()
    {
        Request.RunCallbacks();
    }
}
#endif
