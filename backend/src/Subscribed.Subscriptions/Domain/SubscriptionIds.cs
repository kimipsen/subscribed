namespace Subscribed.Subscriptions.Domain;

/// <summary>Subscription and price-entry ids: 32 lowercase hex characters (a version 7 GUID without dashes).</summary>
public static class SubscriptionIds
{
    public const string StreamPrefix = "subscription-";
    public const int Length = 32;

    public static string New(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        return Guid.CreateVersion7(time.GetUtcNow()).ToString("N");
    }

    /// <summary>Accepts only <c>^[0-9a-f]{32}$</c>.</summary>
    public static bool TryParse(string? value, out string id)
    {
        id = "";
        if (value is not { Length: Length } || !value.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
        {
            return false;
        }

        id = value;
        return true;
    }

    public static string StreamKey(string id) => StreamPrefix + id;

    public static bool TryParseStreamKey(string? streamKey, out string id)
    {
        id = "";
        return streamKey is not null && streamKey.StartsWith(StreamPrefix, StringComparison.Ordinal)
            && TryParse(streamKey[StreamPrefix.Length..], out id);
    }
}
