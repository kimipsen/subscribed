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
