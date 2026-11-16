using Marten;

namespace Subscribed.Subscriptions.PaymentOptions;

/// <summary>Deletes and reprojects every payment option document from its events in one transaction (migrator only).</summary>
public static class PaymentOptionProjectionRebuilder
{
    /// <summary>Returns the number of documents written, removed options included.</summary>
    public static async Task<int> RebuildAsync(IDocumentStore migratorStore, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(migratorStore);
        await using var session = migratorStore.LightweightSession();
        session.DeleteWhere<PaymentOptionState>(_ => true);

        var streamKeys = await session.Events.QueryAllRawEvents()
            .Where(e => e.StreamKey!.StartsWith(PaymentOptionIds.StreamPrefix))
            .Select(e => e.StreamKey!)
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var count = 0;
        foreach (var streamKey in streamKeys.Order(StringComparer.Ordinal))
        {
            var state = await session.Events.AggregateStreamAsync<PaymentOptionState>(streamKey, token: ct).ConfigureAwait(false);
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
