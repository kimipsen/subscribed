namespace Subscribed.Subscriptions.Calendar;

/// <summary>The only human text in the feed, in Danish and English (D12). Names are the one piece of subscription data it carries.</summary>
internal static class CalendarFeedText
{
    public const string Danish = "da";
    public const string English = "en";

    /// <summary>Anything other than exactly <c>da</c> or <c>en</c> (unknown, empty, repeated, too long, other casing) is English.</summary>
    public static string Normalize(string? language) =>
        string.Equals(language, Danish, StringComparison.Ordinal) ? Danish : English;

    public static string CalendarName(string language) =>
        language == Danish ? "Subscribed: opsigelser" : "Subscribed: cancel reminders";

    /// <summary>The reminder 24 hours before the deadline.</summary>
    public static string CancelSoon(string language, string name) =>
        language == Danish ? $"Opsig {name}" : $"Cancel {name}";

    /// <summary>The reminder at the deadline.</summary>
    public static string CancelNow(string language, string name) =>
        language == Danish ? $"Opsig {name} nu" : $"Cancel {name} now";
}
