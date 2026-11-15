using Microsoft.Extensions.Logging;

namespace Subscribed.Subscriptions;

/// <summary>Catalog log messages (source-generated, CA1848).</summary>
public static partial class CatalogLog
{
    public const string Category = "Subscribed.Catalog";

    [LoggerMessage(EventId = 2000, Level = LogLevel.Information, Message = "Catalog loaded: {ServiceCount} services, oldest lastVerified {OldestVerified}")]
    private static partial void Loaded(ILogger logger, int serviceCount, string oldestVerified);

    public static void LogLoaded(ILogger logger, Catalog catalog)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(catalog);
        Loaded(logger, catalog.Services.Count, catalog.OldestVerified?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "n/a");
    }
}
