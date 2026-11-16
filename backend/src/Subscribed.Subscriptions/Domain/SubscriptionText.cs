namespace Subscribed.Subscriptions.Domain;

/// <summary>Normalization and limits for the free-text fields (BR-002, BR-003).</summary>
public static class SubscriptionText
{
    public const int MaxName = 80;
    public const int MaxCategory = 40;
    public const int MaxNotes = 1000;
    public const int MaxUnsubscribeUrl = 2048;

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

    /// <summary>
    /// The one rule for an unsubscribe link, shared by user input and the catalog loader. "Normalize" means trim only: the value is
    /// stored as typed (never through <see cref="Uri"/>, which lowercases the host and appends a slash). Empty or whitespace-only
    /// is absent (null, valid). Otherwise at most 2048 characters, no whitespace or control characters inside, an
    /// <c>https://</c> prefix (ordinal, case-insensitive), an absolute URI with a host and no user-info. A backslash anywhere and an
    /// <c>@</c> in the authority are rejected because <see cref="Uri"/> and a browser's WHATWG parser can disagree on the host.
    /// </summary>
    public static bool TryNormalizeUnsubscribeUrl(string? raw, out string? url)
    {
        url = null;
        var trimmed = (raw ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return true;
        }

        const string scheme = "https://";
        if (trimmed.Length > MaxUnsubscribeUrl
            || trimmed.Any(static c => char.IsWhiteSpace(c) || char.IsControl(c))
            || !trimmed.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        var rest = trimmed.AsSpan(scheme.Length);
        var end = rest.IndexOfAny('/', '?', '#');
        var authority = end < 0 ? rest : rest[..end];
        if (authority.Contains('@'))
        {
            return false;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Host.Length == 0 || uri.UserInfo.Length != 0)
        {
            return false;
        }

        url = trimmed;
        return true;
    }
}
