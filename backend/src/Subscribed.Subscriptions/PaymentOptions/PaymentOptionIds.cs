using Subscribed.Subscriptions.Domain;

namespace Subscribed.Subscriptions.PaymentOptions;

/// <summary>Payment option ids use the subscription id format (32 lowercase hex characters) with their own stream prefix.</summary>
public static class PaymentOptionIds
{
    public const string StreamPrefix = "paymentoption-";

    public static string New(TimeProvider time) => SubscriptionIds.New(time);

    public static bool TryParse(string? value, out string id) => SubscriptionIds.TryParse(value, out id);

    public static string StreamKey(string id) => StreamPrefix + id;

    /// <summary>The bare id of a document id (the stream key) or of an id that is already bare.</summary>
    public static string Bare(string idOrStreamKey)
    {
        ArgumentNullException.ThrowIfNull(idOrStreamKey);
        return idOrStreamKey.StartsWith(StreamPrefix, StringComparison.Ordinal) ? idOrStreamKey[StreamPrefix.Length..] : idOrStreamKey;
    }
}
