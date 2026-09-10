using System;
using System.Collections.Generic;
using System.Threading.Tasks;

/// <summary>
/// Everything the app is allowed to know about who owns what.
///
/// The method list is shaped one-for-one to the Meta Platform SDK's IAP surface on purpose, so
/// swapping the mock for the real thing is a constructor change rather than a redesign:
///
///   InitializeAsync   -> Core.AsyncInitialize + Entitlements.IsUserEntitledToApplication
///                        + IAP.GetViewerPurchases
///   RefreshPricesAsync-> IAP.GetProductsBySKU        (the formatted, LOCALIZED price)
///   PurchaseAsync     -> IAP.LaunchCheckoutFlow
///   RefreshOwnedAsync -> IAP.GetViewerPurchases
///   DisplayName       -> Users.GetLoggedInUser
///
/// One rule keeps the seam honest: **nothing outside Assets/Scripts/Store/ may name
/// Oculus.Platform or PlayerPrefs.** Everything else asks StoreService. Same discipline
/// IGameSession enforces between shared code and the games, and for the same reason.
/// </summary>
public interface IEntitlementService
{
    /// <summary>True when the service came up and Owned can be trusted.</summary>
    Task<bool> InitializeAsync();

    /// <summary>
    /// Fill in each product's price. Meta returns a formatted price in the viewer's currency;
    /// the caller must display exactly what comes back and never compose one itself.
    /// </summary>
    Task RefreshPricesAsync(IEnumerable<string> skus);

    Task<PurchaseResult> PurchaseAsync(string productId, string sku);

    /// <summary>A free product being added to the library. Free products have no SKU.</summary>
    Task<bool> GrantFreeAsync(string productId);

    Task RefreshOwnedAsync();

    IReadOnlyCollection<string> Owned { get; }

    /// <summary>
    /// Membership, as a method rather than leaving callers to call Contains on Owned — which binds
    /// to the ReadOnlySpan&lt;char&gt; extension on IReadOnlyCollection and does not compile.
    /// </summary>
    bool IsOwned(string productId);

    /// <summary>The localized price for a SKU, or null when it has not been fetched.</summary>
    string PriceFor(string sku);

    /// <summary>Raised whenever Owned or a price changed. Panels redraw off this, not per frame.</summary>
    event Action Changed;

    /// <summary>The platform's name for this player, or a remembered one, or "Player".</summary>
    string DisplayName { get; }

    /// <summary>Remember a name the player typed, for the next launch.</summary>
    void RememberDisplayName(string name);
}

/// <summary>
/// Why a purchase ended. All four are distinct because Meta returns all four, and a UI that
/// collapses them shows "purchase failed" to somebody who pressed Back.
/// </summary>
public enum PurchaseOutcome
{
    Succeeded,
    Cancelled,
    AlreadyOwned,
    Failed
}

public struct PurchaseResult
{
    public PurchaseOutcome outcome;
    public string reason;

    public bool Owned => outcome == PurchaseOutcome.Succeeded || outcome == PurchaseOutcome.AlreadyOwned;

    public static PurchaseResult Succeeded() =>
        new PurchaseResult { outcome = PurchaseOutcome.Succeeded };

    public static PurchaseResult Cancelled() =>
        new PurchaseResult { outcome = PurchaseOutcome.Cancelled };

    public static PurchaseResult AlreadyOwned() =>
        new PurchaseResult { outcome = PurchaseOutcome.AlreadyOwned };

    public static PurchaseResult Failed(string reason) =>
        new PurchaseResult { outcome = PurchaseOutcome.Failed, reason = reason };
}
