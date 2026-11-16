using Marten;
using Subscribed.Household;
using Subscribed.Platform.EventStore;

namespace Subscribed.Subscriptions.PaymentOptions;

/// <summary>
/// Payment option reads and writes. One Marten event stream per option (D1). Options are household-owned: an option of another
/// household is never visible (D14). The load-decide-append flow is a small duplicate of <c>SubscriptionService</c> on purpose.
/// </summary>
public sealed class PaymentOptionService(IDocumentStore store, TimeProvider time)
{
    /// <summary>All options of the household, removed ones included, by name.</summary>
    public Task<IReadOnlyList<PaymentOptionState>> ListAsync(CancellationToken ct = default) =>
        EventStoreErrors.GuardAsync<IReadOnlyList<PaymentOptionState>>(async () =>
        {
            await using var session = store.QuerySession();
            var all = await session.Query<PaymentOptionState>().ToListAsync(ct).ConfigureAwait(false);
            return all
                .Where(o => IsOwn(o))
                .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(o => o.Id, StringComparer.Ordinal)
                .ToList();
        });

    /// <summary>One primary-key read. A missing option, or one of another household, is null. A removed option is returned.</summary>
    public Task<PaymentOptionState?> GetAsync(string id, CancellationToken ct = default) =>
        EventStoreErrors.GuardAsync(async () =>
        {
            await using var session = store.QuerySession();
            var state = await session.LoadAsync<PaymentOptionState>(PaymentOptionIds.StreamKey(id), ct).ConfigureAwait(false);
            return state is not null && IsOwn(state) ? state : null;
        });

    public Task<PaymentOptionState> CreateAsync(PaymentOptionFields command, WriteContext context, CancellationToken ct = default) =>
        EventStoreErrors.GuardAsync(async () =>
        {
            var streamKey = PaymentOptionIds.StreamKey(PaymentOptionIds.New(time));
            var events = PaymentOptionDecisions.Create(command, HouseholdService.StreamId, time.GetUtcNow());
            await using var session = store.LightweightSession();
            SetHeaders(session, context);
            session.Events.StartStream<PaymentOptionState>(streamKey, [.. events]);
            await session.SaveChangesAsync(ct).ConfigureAwait(false);
            return await session.LoadAsync<PaymentOptionState>(streamKey, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Payment option view missing after write.");
        });

    public Task<PaymentOptionState> UpdateAsync(string id, long expectedVersion, PaymentOptionFields command, WriteContext context, CancellationToken ct = default) =>
        WriteAsync(id, expectedVersion, history => PaymentOptionDecisions.Change(history, command, time.GetUtcNow()), context, ct);

    /// <summary>Appends <c>payment_option.removed.v1</c>: the document stays, flagged, and nothing is deleted (D3).</summary>
    public Task<PaymentOptionState> RemoveAsync(string id, long expectedVersion, WriteContext context, CancellationToken ct = default) =>
        WriteAsync(id, expectedVersion, history => PaymentOptionDecisions.Remove(history, time.GetUtcNow()), context, ct);

    private static bool IsOwn(PaymentOptionState state) =>
        string.Equals(state.HouseholdId, HouseholdService.StreamId, StringComparison.Ordinal);

    /// <summary>
    /// Fetch and rehydrate → 404 if missing, removed or of another household → stale version is a conflict → decide → append at
    /// version + N → one <c>SaveChanges</c>. A decision with no events skips the append and returns the loaded document.
    /// </summary>
    private Task<PaymentOptionState> WriteAsync(string id, long expectedVersion, Func<IReadOnlyList<object>, IReadOnlyList<object>> decide, WriteContext context, CancellationToken ct) =>
        EventStoreErrors.GuardAsync(async () =>
        {
            var streamKey = PaymentOptionIds.StreamKey(id);
            await using var session = store.LightweightSession();
            var stored = await session.Events.FetchStreamAsync(streamKey, token: ct).ConfigureAwait(false);
            var history = stored.Select(e => e.Data).ToList();
            if (history.Count == 0)
            {
                throw new PaymentOptionNotFoundException();
            }

            var state = PaymentOptionState.Rehydrate(history);
            if (stored[^1].Version != state.Version)
            {
                throw new InvalidOperationException("Stream version does not match the number of applied events.");
            }

            if (state.Removed || !IsOwn(state))
            {
                throw new PaymentOptionNotFoundException();
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

            return await session.LoadAsync<PaymentOptionState>(streamKey, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Payment option view missing after write.");
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
