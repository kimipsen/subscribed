namespace Subscribed.Subscriptions.Domain;

/// <summary>Normalization and limits for the free-text fields (BR-002, BR-003).</summary>
public static class SubscriptionText
{
    public const int MaxName = 80;
    public const int MaxCategory = 40;
    public const int MaxNotes = 1000;

    /// <summary>Trimmed, 1–80 UTF-16 code units, no control characters. Duplicates are allowed (BR-002).</summary>
    public static bool TryNormalizeName(string? raw, out string name)
    {
        name = (raw ?? "").Trim();
        return name.Length is >= 1 and <= MaxName && !name.Any(char.IsControl);
    }

    /// <summary>Empty or whitespace-only means absent (null). Otherwise 1–40 characters, no control characters.</summary>
    public static bool TryNormalizeCategory(string? raw, out string? category)
    {
        category = null;
        var trimmed = (raw ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return true;
        }

        if (trimmed.Length > MaxCategory || trimmed.Any(char.IsControl))
        {
            return false;
        }

        category = trimmed;
        return true;
    }

    /// <summary>CRLF and CR become LF, then trimmed; empty is absent. At most 1,000 characters. LF is allowed, other control characters are not.</summary>
    public static bool TryNormalizeNotes(string? raw, out string? notes)
    {
        notes = null;
        var trimmed = (raw ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();
        if (trimmed.Length == 0)
        {
            return true;
        }

        if (trimmed.Length > MaxNotes || trimmed.Any(static c => char.IsControl(c) && c != '\n'))
        {
            return false;
        }

        notes = trimmed;
        return true;
    }
}
