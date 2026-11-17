using System.Globalization;

namespace Subscribed.Subscriptions.Calendar;

/// <summary>
/// Writes one VTIMEZONE with explicit observances (no RRULE) for the span the feed actually uses. Transitions are found by
/// comparing <see cref="TimeZoneInfo.GetUtcOffset(DateTimeOffset)"/> at a one-hour step and bisecting each change down to the
/// second, so the result follows whatever tz database the server runs on.
/// </summary>
internal static class VTimeZoneWriter
{
    private static readonly TimeSpan Step = TimeSpan.FromHours(1);
    private static readonly TimeSpan Margin = TimeSpan.FromDays(2);

    internal readonly record struct Transition(DateTimeOffset At, TimeSpan From, TimeSpan To, bool ToDaylight);

    /// <summary>Writes the VTIMEZONE for events between <paramref name="earliest"/> and <paramref name="latest"/> (inclusive).</summary>
    public static void Write(IcsWriter writer, TimeZoneInfo zone, string zoneId, DateTimeOffset earliest, DateTimeOffset latest)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(zone);
        var start = WholeSecond(earliest - Margin);
        var end = WholeSecond(latest + Margin);
        var initialOffset = zone.GetUtcOffset(start);

        writer.Begin("VTIMEZONE");
        writer.Property("TZID", zoneId);
        WriteObservance(writer, zone.IsDaylightSavingTime(start), "19700101T000000", initialOffset, initialOffset);
        foreach (var t in FindTransitions(zone, start, end))
        {
            // DTSTART is the onset on the wall clock that was in force before it.
            var local = (t.At + t.From).DateTime.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
            WriteObservance(writer, t.ToDaylight, local, t.From, t.To);
        }

        writer.End("VTIMEZONE");
    }

    internal static IReadOnlyList<Transition> FindTransitions(TimeZoneInfo zone, DateTimeOffset start, DateTimeOffset end)
    {
        var found = new List<Transition>();
        var cursor = start;
        var cursorOffset = zone.GetUtcOffset(cursor);
        while (cursor < end)
        {
            var next = cursor + Step;
            if (next > end)
            {
                next = end;
            }

            if (zone.GetUtcOffset(next) == cursorOffset)
            {
                cursor = next;
                continue;
            }

            // The offset differs somewhere in (cursor, next]: bisect to the first second with the new offset.
            var lo = cursor;
            var hi = next;
            while (hi - lo > TimeSpan.FromSeconds(1))
            {
                var mid = lo + TimeSpan.FromSeconds(Math.Floor((hi - lo).TotalSeconds / 2));
                if (zone.GetUtcOffset(mid) == cursorOffset)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid;
                }
            }

            var after = zone.GetUtcOffset(hi);
            found.Add(new Transition(hi, cursorOffset, after, zone.IsDaylightSavingTime(hi)));
            cursor = hi;
            cursorOffset = after;
        }

        return found;
    }

    /// <summary>The <c>±HHMM</c> form (<c>±HHMMSS</c> only when seconds are non-zero).</summary>
    internal static string FormatOffset(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? '-' : '+';
        var abs = offset.Duration();
        var text = string.Create(CultureInfo.InvariantCulture, $"{sign}{abs.Hours:00}{abs.Minutes:00}");
        return abs.Seconds == 0 ? text : text + abs.Seconds.ToString("00", CultureInfo.InvariantCulture);
    }

    private static void WriteObservance(IcsWriter writer, bool daylight, string dtStart, TimeSpan from, TimeSpan to)
    {
        var kind = daylight ? "DAYLIGHT" : "STANDARD";
        writer.Begin(kind);
        writer.Property("DTSTART", dtStart);
        writer.Property("TZOFFSETFROM", FormatOffset(from));
        writer.Property("TZOFFSETTO", FormatOffset(to));
        writer.End(kind);
    }

    private static DateTimeOffset WholeSecond(DateTimeOffset value)
    {
        var utc = value.UtcDateTime;
        return new DateTimeOffset(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
    }
}
