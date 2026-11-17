using System.Text.RegularExpressions;

namespace Subscribed.Household.Domain;

/// <summary>
/// Household time zone rule (plan D13). Static and pure: no DI. A zone id is valid when it has the IANA shape, is at most
/// 64 characters and is in the server's list of zones; <c>UTC</c> is always valid, even when no tz database exists.
/// </summary>
public static partial class TimeZoneIds
{
    public const string Utc = "UTC";
    public const int MaxLength = 64;

    private static readonly Lazy<IReadOnlyList<string>> SystemList = new(() => ListFrom(TimeZoneInfo.GetSystemTimeZones()));

    /// <summary>The server's valid zone ids: sorted, cached, no <c>posix/</c> or <c>right/</c> entries, no duplicates, always with UTC.</summary>
    public static IReadOnlyList<string> All => SystemList.Value;

    /// <summary>Builds the id list from a zone source. The seam that lets tests restrict or empty the tz database.</summary>
    public static IReadOnlyList<string> ListFrom(IEnumerable<TimeZoneInfo> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var ids = new HashSet<string>(StringComparer.Ordinal) { Utc };
        foreach (var zone in source)
        {
            if (HasValidShape(zone.Id) && !IsExcludedPrefix(zone.Id))
            {
                ids.Add(zone.Id);
            }
        }

        return ids.Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>Returns the id when valid for the server's zone list, otherwise throws <c>invalid-time-zone</c> on field <c>timeZoneId</c>.</summary>
    public static string Normalize(string? id) => Normalize(id, All);

    /// <summary>As <see cref="Normalize(string?)"/>, against a given zone source.</summary>
    public static string Normalize(string? id, IEnumerable<TimeZoneInfo> source) => Normalize(id, ListFrom(source));

    private static string Normalize(string? id, IReadOnlyList<string> valid)
    {
        if (!HasValidShape(id) || !valid.Contains(id!, StringComparer.Ordinal))
        {
            throw new HouseholdDomainException(HouseholdErrorCodes.InvalidTimeZone, "timeZoneId", "Unknown time zone.");
        }

        return id!;
    }

    /// <summary>Resolves a stored id. Returns false for any id that is malformed or that this server cannot resolve.</summary>
    public static bool TryResolve(string? id, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (string.Equals(id, Utc, StringComparison.Ordinal))
        {
            return true;
        }

        if (!HasValidShape(id) || IsExcludedPrefix(id!) || !TimeZoneInfo.TryFindSystemTimeZoneById(id!, out var found))
        {
            return false;
        }

        zone = found;
        return true;
    }

    private static bool HasValidShape(string? id) => id is { Length: > 0 and <= MaxLength } && ZoneIdShape().IsMatch(id);

    private static bool IsExcludedPrefix(string id) =>
        id.StartsWith("posix/", StringComparison.Ordinal) || id.StartsWith("right/", StringComparison.Ordinal);

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_+-]*(/[A-Za-z0-9_+-]+){0,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex ZoneIdShape();
}
