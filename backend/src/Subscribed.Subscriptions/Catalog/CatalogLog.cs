using System.Globalization;
using Microsoft.Extensions.Logging;
using Subscribed.Subscriptions.Domain;

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

    public const int MaxDanglingExamples = 5;

    [LoggerMessage(EventId = 2001, Level = LogLevel.Warning, Message = "Subscription {SubscriptionId} links to catalog id {CatalogId}, which is not in the catalog")]
    private static partial void DanglingDetail(ILogger logger, string subscriptionId, string catalogId);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Warning, Message = "{Count} subscriptions link to catalog ids that are not in the catalog; first {Shown} (subscriptionId, catalogId): {Examples}")]
    private static partial void DanglingOverview(ILogger logger, int count, int shown, string examples);

    /// <summary>One warning per read of a subscription whose stored catalog id is missing from the catalog (R2.4). The read still succeeds.</summary>
    public static void WarnIfDangling(ILogger logger, SubscriptionState state, Catalog catalog)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(catalog);
        if (state.CatalogId is { } id && catalog.TryGet(id) is null)
        {
            DanglingDetail(logger, SubscriptionIds.Bare(state.Id), id);
        }
    }

    /// <summary>At most one warning per overview request: the count plus up to <see cref="MaxDanglingExamples"/> pairs.</summary>
    public static void WarnIfAnyDangling(ILogger logger, IReadOnlyList<SubscriptionState> subscriptions, Catalog catalog)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(subscriptions);
        ArgumentNullException.ThrowIfNull(catalog);
        var dangling = subscriptions.Where(s => s.CatalogId is not null && catalog.TryGet(s.CatalogId) is null).ToList();
        if (dangling.Count == 0)
        {
            return;
        }

        var examples = dangling.Take(MaxDanglingExamples).Select(s => string.Create(CultureInfo.InvariantCulture, $"({SubscriptionIds.Bare(s.Id)}, {s.CatalogId})")).ToList();
        DanglingOverview(logger, dangling.Count, examples.Count, string.Join(", ", examples));
    }
}
