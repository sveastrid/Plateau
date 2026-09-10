using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

/// <summary>
/// The development store: PlayerPrefs behind the same interface the Meta Platform SDK will sit
/// behind. Everything in the lobby, the library panel and the room library is built and tested
/// against this, and §10 of docs/LobbyUpdate.md swaps this one class.
///
/// **This and MetaEntitlementService are the only two files in the project allowed to name
/// PlayerPrefs or Oculus.Platform.** Everything else asks StoreService.
/// </summary>
public class MockEntitlementService : IEntitlementService
{
    private const string OwnedPrefix = "MRBG.owned.";
    private const string NamePref = "MRBG.displayName";

    private readonly HashSet<string> owned = new HashSet<string>();
    private readonly Dictionary<string, string> prices = new Dictionary<string, string>();

    public event Action Changed;

    public IReadOnlyCollection<string> Owned => owned;

    public bool IsOwned(string productId) =>
        !string.IsNullOrEmpty(productId) && owned.Contains(productId);

    public string DisplayName { get; private set; } = "Player";

    public Task<bool> InitializeAsync()
    {
        MockStoreSettings settings = MockStoreSettings.Instance;

        if (settings.clearLibraryOnNextRun)
        {
            // Cleared here rather than left set, so a tester who ticks it gets one wipe and not a
            // library that refuses to persist until they remember to untick it.
            settings.clearLibraryOnNextRun = false;
            ForgetEverything();
        }

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

                if (PlayerPrefs.GetString(OwnedPrefix + module.productId, "") == "1")
                {
                    owned.Add(module.productId);
                }
            }
        }

        string remembered = PlayerPrefs.GetString(NamePref, "");
        DisplayName = string.IsNullOrEmpty(remembered) ? "Player" : remembered;

        Raise();
        return Task.FromResult(true);
    }

    /// <summary>
    /// The mock's only source of a price is the module's mockPriceLabel. The real service must
    /// overwrite what it puts here and must never fall back to it — see GameModule.mockPriceLabel.
    /// </summary>
    public Task RefreshPricesAsync(IEnumerable<string> skus)
    {
        GameCatalog catalog = GameCatalog.Instance;
        if (catalog == null || skus == null)
        {
            return Task.CompletedTask;
        }

        foreach (string sku in skus)
        {
            if (string.IsNullOrEmpty(sku))
            {
                continue;
            }

            for (int i = 0; i < catalog.games.Count; i++)
            {
                GameModule module = catalog.games[i];
                if (module != null && module.metaSku == sku)
                {
                    prices[sku] = string.IsNullOrEmpty(module.mockPriceLabel)
                                      ? "Buy"
                                      : module.mockPriceLabel;
                    break;
                }
            }
        }

        Raise();
        return Task.CompletedTask;
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
        if (string.IsNullOrEmpty(productId))
        {
            return PurchaseResult.Failed("no product id");
        }

        if (owned.Contains(productId))
        {
            return PurchaseResult.AlreadyOwned();
        }

        MockStoreSettings settings = MockStoreSettings.Instance;

        int ms = Mathf.RoundToInt(Mathf.Max(0f, settings.simulatedLatencySeconds) * 1000f);
        if (ms > 0)
        {
            await Task.Delay(ms);
        }

        if (settings.forceCancel)
        {
            return PurchaseResult.Cancelled();
        }

        if (settings.forceFailure)
        {
            return PurchaseResult.Failed("the mock store is set to force failures");
        }

        Write(productId);
        return PurchaseResult.Succeeded();
    }

    public Task<bool> GrantFreeAsync(string productId)
    {
        if (string.IsNullOrEmpty(productId))
        {
            return Task.FromResult(false);
        }

        Write(productId);
        return Task.FromResult(true);
    }

    public Task RefreshOwnedAsync()
    {
        return InitializeAsync();
    }

    public void RememberDisplayName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        DisplayName = name;
        PlayerPrefs.SetString(NamePref, name);
        PlayerPrefs.Save();
        Raise();
    }

    private void Write(string productId)
    {
        owned.Add(productId);
        PlayerPrefs.SetString(OwnedPrefix + productId, "1");
        PlayerPrefs.Save();
        Raise();
    }

    private void ForgetEverything()
    {
        GameCatalog catalog = GameCatalog.Instance;
        if (catalog == null)
        {
            return;
        }

        for (int i = 0; i < catalog.games.Count; i++)
        {
            GameModule module = catalog.games[i];
            if (module != null && !string.IsNullOrEmpty(module.productId))
            {
                PlayerPrefs.DeleteKey(OwnedPrefix + module.productId);
            }
        }
        PlayerPrefs.Save();
    }

    private void Raise()
    {
        Changed?.Invoke();
    }
}
