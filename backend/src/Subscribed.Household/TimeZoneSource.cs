namespace Subscribed.Household;

/// <summary>
/// The server's valid time zone ids, behind a seam so a test can restrict or empty the tz database without touching validation
/// (plan section 6, tzdata). Read by the zone list endpoint and by readiness; the aggregate keeps using the static
/// <see cref="Domain.TimeZoneIds"/> rule.
/// </summary>
public interface ITimeZoneSource
{
    /// <summary>Sorted valid zone ids. May be empty only for a replaced source: the default always lists <c>UTC</c>.</summary>
    IReadOnlyList<string> Ids { get; }
}

/// <summary>Default source: the cached list built from <c>TimeZoneInfo.GetSystemTimeZones()</c>.</summary>
public sealed class SystemTimeZoneSource : ITimeZoneSource
{
    public IReadOnlyList<string> Ids => Domain.TimeZoneIds.All;
}

public static class TimeZoneSourceExtensions
{
    /// <summary>
    /// True when the source lists no zone other than <c>UTC</c>, which is what a runtime image without tzdata looks like.
    /// Only UTC households work then.
    /// </summary>
    public static bool IsMissingTzData(this ITimeZoneSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return !source.Ids.Any(id => !string.Equals(id, Domain.TimeZoneIds.Utc, StringComparison.Ordinal));
    }
}
