using Microsoft.Extensions.Logging;

namespace Subscribed.Household;

/// <summary>
/// Content-free calendar feed signals. Only the household id is ever logged: never the secret, its hash, a zone id or any request
/// input. Source-generated (CA1848).
/// </summary>
public static partial class HouseholdLog
{
    public const string Category = "Subscribed.Household";

    [LoggerMessage(EventId = 3100, Level = LogLevel.Information, Message = "household.calendar_feed_rotated {HouseholdId}")]
    private static partial void FeedRotatedCore(ILogger logger, string householdId);

    [LoggerMessage(EventId = 3101, Level = LogLevel.Information, Message = "household.calendar_feed_revoked {HouseholdId}")]
    private static partial void FeedRevokedCore(ILogger logger, string householdId);

    [LoggerMessage(EventId = 3102, Level = LogLevel.Warning, Message = "household.time_zone_unresolvable {HouseholdId}; answering in UTC")]
    private static partial void TimeZoneUnresolvableCore(ILogger logger, string householdId);

    [LoggerMessage(EventId = 3103, Level = LogLevel.Warning, Message = "household.time_zone_data_missing: the server lists no time zone but UTC; only UTC households work")]
    private static partial void TimeZoneDataMissingCore(ILogger logger);

    /// <summary>Startup signal: no tz database in this runtime image. Carries no data at all.</summary>
    public static void TimeZoneDataMissing(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        TimeZoneDataMissingCore(logger);
    }

    /// <summary>The stored zone id cannot be resolved on this server, so the request answers in UTC. Carries the household id only.</summary>
    public static void TimeZoneUnresolvable(ILogger logger, string householdId)
    {
        ArgumentNullException.ThrowIfNull(logger);
        TimeZoneUnresolvableCore(logger, householdId);
    }

    public static void FeedRotated(ILogger logger, string householdId)
    {
        ArgumentNullException.ThrowIfNull(logger);
        FeedRotatedCore(logger, householdId);
    }

    public static void FeedRevoked(ILogger logger, string householdId)
    {
        ArgumentNullException.ThrowIfNull(logger);
        FeedRevokedCore(logger, householdId);
    }
}
