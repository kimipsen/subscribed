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

    /// <summary>The bare id of a document id (the stream key) or of an id that is already bare.</summary>
    public static string Bare(string idOrStreamKey)
    {
        ArgumentNullException.ThrowIfNull(idOrStreamKey);
        return idOrStreamKey.StartsWith(StreamPrefix, StringComparison.Ordinal) ? idOrStreamKey[StreamPrefix.Length..] : idOrStreamKey;
    }

    public static bool TryParseStreamKey(string? streamKey, out string id)
    {
        id = "";
        return streamKey is not null && streamKey.StartsWith(StreamPrefix, StringComparison.Ordinal)
            && TryParse(streamKey[StreamPrefix.Length..], out id);
    }
}

/// <summary>
/// Pause period ids: 32 lowercase hex characters for a period created by the API, or <c>legacy-n</c> (one to four digits) for a
/// period rebuilt from the legacy pause events. <c>legacy-0</c> matches the shape but can never exist (ids start at 1).
/// </summary>
public static class PausePeriodIds
{
    public const string LegacyPrefix = "legacy-";
    public const int MaxLegacyDigits = 4;

    public static bool TryParse(string? value, out string id)
    {
        id = "";
        if (SubscriptionIds.TryParse(value, out id))
        {
            return true;
        }

        if (value is not null && value.StartsWith(LegacyPrefix, StringComparison.Ordinal))
        {
            var digits = value.AsSpan(LegacyPrefix.Length);
            if (digits.Length is >= 1 and <= MaxLegacyDigits && digits.IndexOfAnyExceptInRange('0', '9') < 0)
            {
                id = value;
                return true;
            }
        }

        id = "";
        return false;
    }
}
