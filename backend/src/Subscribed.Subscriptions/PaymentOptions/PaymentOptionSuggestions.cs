namespace Subscribed.Subscriptions.PaymentOptions;

public sealed record PaymentOptionSuggestion(string Id, string Name);

/// <summary>A static server-side list (D5). Suggestions prefill a name only: no suggestion id is stored (D6). Brand names are not translated.</summary>
public static class PaymentOptionSuggestions
{
    public static IReadOnlyList<PaymentOptionSuggestion> All { get; } =
    [
        new("paypal", "PayPal"),
        new("visa", "Visa"),
        new("mastercard", "Mastercard"),
        new("dankort", "Dankort"),
        new("mobilepay-vipps", "MobilePay / Vipps"),
    ];
}
