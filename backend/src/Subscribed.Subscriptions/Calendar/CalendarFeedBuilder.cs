using System.Globalization;
using System.Text.RegularExpressions;
using Subscribed.Subscriptions.Domain;

namespace Subscribed.Subscriptions.Calendar;

/// <summary>
/// Builds the household's iCal feed (D9 to D12). Pure: no HTTP, no clock, no I/O. The caller passes the resolved zone, its id and
/// the current time, so the same input always gives the same text. The feed carries subscription names and dates and nothing else (R3.5).
/// </summary>
public static partial class CalendarFeedBuilder
{
    private const string UidSuffix = "@subscribed";
    private static readonly TimeSpan Lead = TimeSpan.FromHours(24);

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_+-]*(/[A-Za-z0-9_+-]+){0,2}$")]
    private static partial Regex SafeZoneId();

    /// <summary>
    /// Builds the VCALENDAR text. <paramref name="zone"/> and <paramref name="zoneId"/> come from the caller; when the stored id could
    /// not be resolved the caller passes <see cref="TimeZoneInfo.Utc"/> and the feed then says UTC throughout. <paramref name="language"/>
    /// is normalised: anything but exactly <c>da</c> or <c>en</c> is English.
    /// </summary>
    public static string Build(
        IReadOnlyList<SubscriptionState> subscriptions,
        TimeZoneInfo zone,
        string zoneId,
        string? language,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(zoneId);

        var lang = CalendarFeedText.Normalize(language);
        var nowUtc = now.ToUniversalTime();
        var utc = zone.Equals(TimeZoneInfo.Utc) || !SafeZoneId().IsMatch(zoneId);
        if (utc)
        {
            zone = TimeZoneInfo.Utc;
            zoneId = "UTC";
        }

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, zone).DateTime);
        var clampEnd = nowUtc.AddYears(10);

        var events = Eligible(subscriptions, today, nowUtc)
            .SelectMany(s => Events(s, lang))
            .Select(e => e with { UsesZone = !utc && UsesZone(e.At, zone, clampEnd) })
            .ToList();

        var w = new IcsWriter();
        w.Begin("VCALENDAR");
        w.Property("VERSION", "2.0");
        w.Property("PRODID", "-//subscribed//cancel-reminders//EN");
        w.Property("CALSCALE", "GREGORIAN");
        w.Property("METHOD", "PUBLISH");
        w.Text("X-WR-CALNAME", CalendarFeedText.CalendarName(lang));
        w.Property("X-WR-TIMEZONE", zoneId);
        w.Property("REFRESH-INTERVAL;VALUE=DURATION", "PT12H");
        w.Property("X-PUBLISHED-TTL", "PT12H");

        var zoned = events.Where(e => e.UsesZone).Select(e => e.At).ToList();
        if (zoned.Count > 0)
        {
            VTimeZoneWriter.Write(w, zone, zoneId, zoned.Min(), zoned.Max());
        }

        var stamp = Utc(nowUtc);
        foreach (var e in events)
        {
            w.Begin("VEVENT");
            w.Property("UID", e.Uid);
            w.Property("DTSTAMP", stamp);
            w.Property("SEQUENCE", e.Sequence.ToString(CultureInfo.InvariantCulture));
            if (e.UsesZone)
            {
                var local = TimeZoneInfo.ConvertTime(e.At, zone).DateTime;
                w.Property($"DTSTART;TZID={zoneId}", local.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture));
            }
            else
            {
                w.Property("DTSTART", Utc(e.At));
            }

            w.Property("DURATION", "PT15M");
            w.Text("SUMMARY", e.Summary);
            w.End("VEVENT");
        }

        w.End("VCALENDAR");
        return w.ToString();
    }

    /// <summary>D9: not removed, a deadline that has not passed, and not ended on today's date in the household zone. Stable order.</summary>
    private static IEnumerable<SubscriptionState> Eligible(IReadOnlyList<SubscriptionState> subscriptions, DateOnly today, DateTimeOffset now) =>
        subscriptions
            .Where(s => !s.Removed
                && s.CancelBy is { } cancelBy && cancelBy >= now
                && !SubscriptionCalculator.IsEnded(s, today))
            .OrderBy(s => s.CancelBy)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .ThenBy(s => SubscriptionIds.Bare(s.Id), StringComparer.Ordinal);

    private static IEnumerable<FeedEvent> Events(SubscriptionState s, string lang)
    {
        var id = SubscriptionIds.Bare(s.Id);
        var cancelBy = s.CancelBy!.Value.ToUniversalTime();
        yield return new FeedEvent($"{id}-cancel-24h{UidSuffix}", cancelBy - Lead, s.Version, CalendarFeedText.CancelSoon(lang, s.Name), false);
        yield return new FeedEvent($"{id}-cancel-at{UidSuffix}", cancelBy, s.Version, CalendarFeedText.CancelNow(lang, s.Name), false);
    }

    /// <summary>
    /// D11: an event is written with TZID unless its local time is ambiguous (DST overlap) or it lies beyond the ten-year clamp.
    /// Those two are written in UTC, so no client can read them two ways and the VTIMEZONE scan stays bounded.
    /// </summary>
    private static bool UsesZone(DateTimeOffset at, TimeZoneInfo zone, DateTimeOffset clampEnd) =>
        at <= clampEnd && !zone.IsAmbiguousTime(TimeZoneInfo.ConvertTime(at, zone).DateTime);

    private static string Utc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    private sealed record FeedEvent(string Uid, DateTimeOffset At, long Sequence, string Summary, bool UsesZone);
}
