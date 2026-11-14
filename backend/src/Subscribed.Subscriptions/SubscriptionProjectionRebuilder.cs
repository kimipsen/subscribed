using Marten;
using Subscribed.Subscriptions.Domain;

namespace Subscribed.Subscriptions;

/// <summary>Deletes and reprojects every subscription document from its events in one transaction (migrator only).</summary>
public static class SubscriptionProjectionRebuilder
{
    /// <summary>Returns the number of documents written, removed subscriptions included.</summary>
    public static async Task<int> RebuildAsync(IDocumentStore migratorStore, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(migratorStore);
        await using var session = migratorStore.LightweightSession();
        session.DeleteWhere<SubscriptionState>(_ => true);

        var streamKeys = await session.Events.QueryAllRawEvents()
            .Where(e => e.StreamKey!.StartsWith(SubscriptionIds.StreamPrefix))
            .Select(e => e.StreamKey!)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var count = 0;
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
        }

        await session.SaveChangesAsync(ct).ConfigureAwait(false);
        return count;
    }
}
