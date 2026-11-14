using Marten;
using Subscribed.Household;
using Subscribed.Platform.EventStore;
using Subscribed.Subscriptions.Domain;

namespace Subscribed.Subscriptions;

/// <summary>Subscription reads and writes. One Marten event stream per subscription (D-3).</summary>
public sealed class SubscriptionService(IDocumentStore store, TimeProvider time)
{
    /// <summary>All non-removed subscriptions of the household, in no particular order.</summary>
    public Task<IReadOnlyList<SubscriptionState>> ListAsync(CancellationToken ct = default) =>
        EventStoreErrors.GuardAsync<IReadOnlyList<SubscriptionState>>(async () =>
        {
            await using var session = store.QuerySession();
            var all = await session.Query<SubscriptionState>().ToListAsync(ct).ConfigureAwait(false);
            return all.Where(s => !s.Removed && string.Equals(s.HouseholdId, HouseholdService.StreamId, StringComparison.Ordinal)).ToList();
        });

    /// <summary>One primary-key read. A removed or missing subscription is null.</summary>
    public Task<SubscriptionState?> GetAsync(string id, CancellationToken ct = default) =>
        EventStoreErrors.GuardAsync(async () =>
        {
            await using var session = store.QuerySession();
            var state = await session.LoadAsync<SubscriptionState>(SubscriptionIds.StreamKey(id), ct).ConfigureAwait(false);
            return state is { Removed: false } ? state : null;
        });

    public Task<SubscriptionState> AddAsync(NewSubscription command, WriteContext context, CancellationToken ct = default) =>
        EventStoreErrors.GuardAsync(async () =>
        {
            var id = SubscriptionIds.New(time);
            var streamKey = SubscriptionIds.StreamKey(id);
            var events = SubscriptionDecisions.Add(command, HouseholdService.StreamId, SubscriptionIds.New(time), time.GetUtcNow());
            await using var session = store.LightweightSession();
            SetHeaders(session, context);
            session.Events.StartStream<SubscriptionState>(streamKey, [.. events]);
            await session.SaveChangesAsync(ct).ConfigureAwait(false);
            return await session.LoadAsync<SubscriptionState>(streamKey, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Subscription view missing after write.");
        });

    public Task<SubscriptionState> UpdateAsync(string id, long expectedVersion, DetailsUpdate command, WriteContext context, CancellationToken ct = default) =>
        WriteAsync(id, expectedVersion, history => SubscriptionDecisions.UpdateDetails(history, command, time.GetUtcNow()), context, ct);

    public Task<SubscriptionState> AddPriceEntryAsync(string id, long expectedVersion, PriceEntryChange command, WriteContext context, CancellationToken ct = default) =>
        WriteAsync(id, expectedVersion, history => SubscriptionDecisions.AddPriceEntry(history, command, SubscriptionIds.New(time), time.GetUtcNow()), context, ct);

    public Task<SubscriptionState> CorrectPriceEntryAsync(string id, long expectedVersion, string entryId, PriceEntryChange command, WriteContext context, CancellationToken ct = default) =>
        WriteAsync(id, expectedVersion, history => SubscriptionDecisions.CorrectPriceEntry(history, entryId, command, time.GetUtcNow()), context, ct);

    public Task<SubscriptionState> RemovePriceEntryAsync(string id, long expectedVersion, string entryId, WriteContext context, CancellationToken ct = default) =>
        WriteAsync(id, expectedVersion, history => SubscriptionDecisions.RemovePriceEntry(history, entryId, time.GetUtcNow()), context, ct);

    /// <summary>Appends <c>subscription.removed.v1</c>: the document stays, flagged, and nothing is deleted (D-5).</summary>
    public Task<SubscriptionState> RemoveAsync(string id, long expectedVersion, WriteContext context, CancellationToken ct = default) =>
        WriteAsync(id, expectedVersion, history => SubscriptionDecisions.Remove(history, time.GetUtcNow()), context, ct);

    /// <summary>
    /// Fetch and rehydrate → 404 if missing or removed → stale version is a conflict → decide → append at version + N → one
    /// <c>SaveChanges</c>. A decision with no events skips the append and returns the loaded document (no new version).
    /// </summary>
    private Task<SubscriptionState> WriteAsync(string id, long expectedVersion, Func<IReadOnlyList<object>, IReadOnlyList<object>> decide, WriteContext context, CancellationToken ct) =>
        EventStoreErrors.GuardAsync(async () =>
        {
            var streamKey = SubscriptionIds.StreamKey(id);
            await using var session = store.LightweightSession();
            var stored = await session.Events.FetchStreamAsync(streamKey, token: ct).ConfigureAwait(false);
            var history = stored.Select(e => e.Data).ToList();
            if (history.Count == 0)
            {
                throw new SubscriptionNotFoundException();
            }

            var state = SubscriptionState.Rehydrate(history);
            if (stored[^1].Version != state.Version)
            {
                throw new InvalidOperationException("Stream version does not match the number of applied events.");
            }

            if (state.Removed)
            {
                throw new SubscriptionNotFoundException();
            }

            if (state.Version != expectedVersion)
            {
                throw new EventStoreException(new EventStoreFailure(EventStoreFailureKind.ConcurrencyConflict), new InvalidOperationException("Stale expected version."));
            }

            var events = decide(history);
            if (events.Count > 0)
            {
                SetHeaders(session, context);

                // Marten's expected version is the stream version AFTER the append.
                session.Events.Append(streamKey, state.Version + events.Count, [.. events]);
                await session.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            return await session.LoadAsync<SubscriptionState>(streamKey, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Subscription view missing after write.");
        });

    private static void SetHeaders(IDocumentSession session, WriteContext context)
    {
        session.SetHeader("actor", context.Actor);
        if (!string.IsNullOrEmpty(context.CorrelationId))
        {
            session.SetHeader("correlationId", context.CorrelationId);
        }
    }
}
