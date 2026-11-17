namespace Subscribed.Subscriptions.PaymentOptions;

public sealed record PaymentOptionSuggestion(string Id, string Name);

/// <summary>
/// A static server-side list (D5), built from <see cref="PaymentOptionBrands"/>. The suggestion id is the brand id: a chip
/// prefills the name and the client stores the brand on create. Brand names are not translated.
/// </summary>
public static class PaymentOptionSuggestions
{
    public static IReadOnlyList<PaymentOptionSuggestion> All { get; } =
        [.. PaymentOptionBrands.All.Select(static b => new PaymentOptionSuggestion(b.Id, b.Name))];
}
