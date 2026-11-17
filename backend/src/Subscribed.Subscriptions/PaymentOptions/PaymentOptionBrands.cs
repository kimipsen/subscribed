namespace Subscribed.Subscriptions.PaymentOptions;

/// <summary>
/// The known payment option brands: the single source for the ids a payment option may link to and for the suggestion list.
/// A brand is a stable id; the logo for it is frontend data. Brand names are not translated.
/// </summary>
public static class PaymentOptionBrands
{
    public static IReadOnlyList<(string Id, string Name)> All { get; } =
    [
        ("paypal", "PayPal"),
        ("visa", "Visa"),
        ("mastercard", "Mastercard"),
        ("dankort", "Dankort"),
        ("mobilepay-vipps", "MobilePay / Vipps"),
    ];

    /// <summary>The known ids, in the order of <see cref="All"/>.</summary>
    public static IReadOnlyList<string> Ids { get; } = [.. All.Select(static b => b.Id)];

    /// <summary>Exact, ordinal match: no trimming and no case folding. Null is not a known id (it means no brand).</summary>
    public static bool IsKnown(string? id) =>
        id is not null && Ids.Contains(id, StringComparer.Ordinal);
}
