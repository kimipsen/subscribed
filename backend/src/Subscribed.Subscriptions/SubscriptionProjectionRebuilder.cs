using System.Globalization;
using Marten;
using Microsoft.Extensions.Logging;
using Subscribed.Household;
using Subscribed.Household.Domain;
using Subscribed.Subscriptions.Domain;

namespace Subscribed.Subscriptions;

/// <summary>
/// What the rebuild did to legacy pause data, as counts an operator can compare before and after. <c>converted</c> legacy pause
/// periods became dated periods, <c>dropped_zero_day</c> ones resolved to a zero-day pause (D7) and are omitted, <c>orphan_resumes</c>
/// legacy resumes found no open pause and were ignored, and <c>zone_fallback</c> is 1 when the household zone could not be resolved
/// and UTC was used.
/// </summary>
public sealed record PauseRebuildSummary(int Converted, int DroppedZeroDay, int OrphanResumes, int ZoneFallback)
{
    public string ToLine() => string.Create(CultureInfo.InvariantCulture,
        $"pause-rebuild converted={Converted} dropped_zero_day={DroppedZeroDay} orphan_resumes={OrphanResumes} zone_fallback={ZoneFallback}");
}

/// <summary>The number of documents written (removed subscriptions included) and the pause conversion counts.</summary>
public sealed record SubscriptionRebuildResult(int Documents, PauseRebuildSummary Pause);

/// <summary>Deletes and reprojects every subscription document from its events in one transaction (migrator only).</summary>
public static partial class SubscriptionProjectionRebuilder
{
    public const string LogCategory = "Subscribed.Subscriptions.Rebuild";

    /// <summary>Returns the number of documents written, removed subscriptions included.</summary>
    public static async Task<int> RebuildAsync(IDocumentStore migratorStore, CancellationToken ct = default) =>
        (await RebuildWithSummaryAsync(migratorStore, null, null, ct).ConfigureAwait(false)).Documents;

    /// <summary>
    /// Like <see cref="RebuildAsync"/>, and also returns (and logs, when a logger is given) the legacy pause conversion counts.
    /// Legacy instants are turned into dates in the household zone and clamped to the UTC today (from <paramref name="time"/>), exactly as the API does on read.
    /// </summary>
    public static async Task<SubscriptionRebuildResult> RebuildWithSummaryAsync(IDocumentStore migratorStore, ILogger? logger, TimeProvider? time = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(migratorStore);
        await using var session = migratorStore.LightweightSession();
        session.DeleteWhere<SubscriptionState>(_ => true);

        var household = await session.Events.AggregateStreamAsync<HouseholdView>(HouseholdService.StreamId, token: ct).ConfigureAwait(false);
        var zoneResolved = TimeZoneIds.TryResolve(household?.TimeZoneId ?? TimeZoneIds.Utc, out var zone);
        if (!zoneResolved)
        {
            zone = TimeZoneInfo.Utc;
        }

        var today = SubscriptionCalculator.Today(time ?? TimeProvider.System);

        var streamKeys = await session.Events.QueryAllRawEvents()
            .Where(e => e.StreamKey!.StartsWith(SubscriptionIds.StreamPrefix))
            .Select(e => e.StreamKey!)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var count = 0;
        var converted = 0;
        var dropped = 0;
        var orphans = 0;
        foreach (var streamKey in streamKeys.Order(StringComparer.Ordinal))
        {
            var state = await session.Events.AggregateStreamAsync<SubscriptionState>(streamKey, token: ct).ConfigureAwait(false);
            if (state is null)
            {
                continue;
            }

            state.Id = streamKey;
            session.Store(state);
            count++;

            var resolved = state.ResolvePausePeriods(zone, today).Select(p => p.PeriodId).ToHashSet(StringComparer.Ordinal);
            foreach (var period in state.PausePeriods.Where(p => p.PausedInstant is not null))
            {
                if (resolved.Contains(period.PeriodId))
                {
                    converted++;
                }
                else
                {
                    dropped++;
                }
            }

            orphans += state.OrphanLegacyResumes;
        }

        await session.SaveChangesAsync(ct).ConfigureAwait(false);

        var summary = new PauseRebuildSummary(converted, dropped, orphans, zoneResolved ? 0 : 1);
        if (logger is not null)
        {
            Summarized(logger, summary.Converted, summary.DroppedZeroDay, summary.OrphanResumes, summary.ZoneFallback);
        }

        return new SubscriptionRebuildResult(count, summary);
    }

    [LoggerMessage(EventId = 2100, Level = LogLevel.Information,
        Message = "Pause rebuild: {Converted} legacy pause periods converted, {DroppedZeroDay} zero-day periods dropped, {OrphanResumes} orphan resumes ignored, zone fallback {ZoneFallback}")]
    private static partial void Summarized(ILogger logger, int converted, int droppedZeroDay, int orphanResumes, int zoneFallback);
}
