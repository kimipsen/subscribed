using Marten;
using Microsoft.Extensions.Logging;
using Subscribed.Household;
using Subscribed.Platform.EventStore;
using Subscribed.Subscriptions.Domain;
using Subscribed.Subscriptions.PaymentOptions;

namespace Subscribed.Subscriptions;

/// <summary>Subscription reads and writes. One Marten event stream per subscription (D-3).</summary>
public sealed class SubscriptionService(IDocumentStore store, TimeProvider time, Catalog catalog, ILoggerFactory? loggers = null)
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
            RequireKnownCatalogId(command.CatalogId);

            // A create has no stream yet, so there is no 404 or 412 ordering to protect and no "only when changed" rule (D13).
            await RequireUsablePaymentOptionAsync(command.PaymentOptionId, ct).ConfigureAwait(false);
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
        WriteAsync(id, expectedVersion, async (history, token) =>
        {
            // Only a changed link is checked, so a subscription whose catalog entry was removed can still be edited. This runs inside
            // the decide step, after the 404 and 412 checks. The extra rehydration is accepted: do not fold it into WriteAsync.
            if (!string.Equals(command.CatalogId, SubscriptionState.Rehydrate(history).CatalogId, StringComparison.Ordinal))
            {
                RequireKnownCatalogId(command.CatalogId);
            }

            // Details and the payment option change are one decision and one append (R7). Only the emitted payment events are
            // checked for usability, so an unchanged removed option never blocks the save.
            var events = SubscriptionDecisions.UpdateDetails(history, command, SubscriptionIds.New(time), time.GetUtcNow());
            await RequireAssignedOptionsUsableAsync(events, token).ConfigureAwait(false);
            LogPaymentChange(command, events);
            return events;
        }, context, ct);

    public Task<SubscriptionState> AddPriceEntryAsync(string id, long expectedVersion, PriceEntryChange command, WriteContext context, CancellationToken ct = default) =>
        WriteAsync(id, expectedVersion, async (history, token) =>
        {
            var events = SubscriptionDecisions.AddPriceEntry(history, command, SubscriptionIds.New(time), time.GetUtcNow());
            await RequireAssignedOptionsUsableAsync(events, token).ConfigureAwait(false);
            return events;
        }, context, ct);

    public Task<SubscriptionState> CorrectPriceEntryAsync(string id, long expectedVersion, string entryId, PriceEntryChange command, WriteContext context, CancellationToken ct = default) =>
        WriteAsync(id, expectedVersion, async (history, token) =>
        {
            var events = SubscriptionDecisions.CorrectPriceEntry(history, entryId, command, time.GetUtcNow());
            await RequireAssignedOptionsUsableAsync(events, token).ConfigureAwait(false);
            return events;
        }, context, ct);

    public Task<SubscriptionState> RemovePriceEntryAsync(string id, long expectedVersion, string entryId, WriteContext context, CancellationToken ct = default) =>
        WriteAsync(id, expectedVersion, history => SubscriptionDecisions.RemovePriceEntry(history, entryId, time.GetUtcNow()), context, ct);

    /// <summary>Appends <c>subscription.removed.v1</c>: the document stays, flagged, and nothing is deleted (D-5).</summary>
    public Task<SubscriptionState> RemoveAsync(string id, long expectedVersion, WriteContext context, CancellationToken ct = default) =>
        WriteAsync(id, expectedVersion, history => SubscriptionDecisions.Remove(history, time.GetUtcNow()), context, ct);

    /// <summary>
    /// Opens a pause period starting today (<c>subscription.pause_period_added.v1</c>). A paused subscription is rejected with
    /// <see cref="SubscriptionStateException"/>. <paramref name="zone"/> is the household zone that resolves legacy pauses.
    /// </summary>
    public Task<SubscriptionState> PauseAsync(string id, long expectedVersion, TimeZoneInfo zone, WriteContext context, CancellationToken ct = default) =>
        WriteAsync(id, expectedVersion, history => SubscriptionDecisions.Pause(history, SubscriptionIds.New(time), SubscriptionCalculator.Today(time), zone, time.GetUtcNow()), context, ct);

    /// <summary>Closes today's open pause period. A subscription that is not paused is rejected with <see cref="SubscriptionStateException"/>.</summary>
    public Task<SubscriptionState> ResumeAsync(string id, long expectedVersion, TimeZoneInfo zone, WriteContext context, CancellationToken ct = default) =>
        WriteAsync(id, expectedVersion, history => SubscriptionDecisions.Resume(history, SubscriptionCalculator.Today(time), zone, time.GetUtcNow()), context, ct);

    public Task<SubscriptionState> AddPausePeriodAsync(string id, long expectedVersion, PauseChange command, TimeZoneInfo zone, WriteContext context, CancellationToken ct = default) =>
        WriteAsync(id, expectedVersion, history => SubscriptionDecisions.AddPausePeriod(history, command, SubscriptionIds.New(time), SubscriptionCalculator.Today(time), zone, time.GetUtcNow()), context, ct);

    public Task<SubscriptionState> ChangePausePeriodAsync(string id, long expectedVersion, string periodId, PauseChange command, TimeZoneInfo zone, WriteContext context, CancellationToken ct = default) =>
        WriteAsync(id, expectedVersion, history => SubscriptionDecisions.ChangePausePeriod(history, periodId, command, SubscriptionCalculator.Today(time), zone, time.GetUtcNow()), context, ct);

    public Task<SubscriptionState> RemovePausePeriodAsync(string id, long expectedVersion, string periodId, TimeZoneInfo zone, WriteContext context, CancellationToken ct = default) =>
        WriteAsync(id, expectedVersion, history => SubscriptionDecisions.RemovePausePeriod(history, periodId, SubscriptionCalculator.Today(time), zone, time.GetUtcNow()), context, ct);

    /// <summary>
    /// Fetch and rehydrate → 404 if missing or removed → stale version is a conflict → decide → append at version + N → one
    /// <c>SaveChanges</c>. A decision with no events skips the append and returns the loaded document (no new version).
    /// </summary>
    private Task<SubscriptionState> WriteAsync(string id, long expectedVersion, Func<IReadOnlyList<object>, IReadOnlyList<object>> decide, WriteContext context, CancellationToken ct) =>
        WriteAsync(id, expectedVersion, (history, _) => Task.FromResult(decide(history)), context, ct);

    /// <summary>
    /// The same flow with an async decide step (D13), for a decision that needs another stream: a database load cannot run in the
    /// sync decide without sync-over-async, and checking before this call would break the 404, 412, then 400 order.
    /// </summary>
    private Task<SubscriptionState> WriteAsync(
        string id,
        long expectedVersion,
        Func<IReadOnlyList<object>, CancellationToken, Task<IReadOnlyList<object>>> decideAsync,
        WriteContext context,
        CancellationToken ct) =>
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

            var events = await decideAsync(history, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Checks every option the candidate events assign (D10): a changed or new assignment must name an existing, non-removed option
    /// of the household. An unchanged reference never produces an event, so an entry that still points at a removed option stays
    /// editable. A concurrent removal after this check is accepted (D15): the id still resolves, shown as removed.
    /// </summary>
    private async Task RequireAssignedOptionsUsableAsync(IReadOnlyList<object> events, CancellationToken ct)
    {
        foreach (var e in events.OfType<SubscriptionPriceEntryPaymentChanged>())
        {
            await RequireUsablePaymentOptionAsync(e.PaymentOptionId, ct).ConfigureAwait(false);
        }
    }

    /// <summary>A null id is "no option" and always allowed. Otherwise a missing, removed or other-household option is a 400 on <c>paymentOptionId</c> (D14).</summary>
    private async Task RequireUsablePaymentOptionAsync(string? paymentOptionId, CancellationToken ct)
    {
        if (paymentOptionId is null)
        {
            return;
        }

        await using var session = store.QuerySession();
        var option = await session.LoadAsync<PaymentOptionState>(PaymentOptionIds.StreamKey(paymentOptionId), ct).ConfigureAwait(false);
        if (option is null || option.Removed || !string.Equals(option.HouseholdId, HouseholdService.StreamId, StringComparison.Ordinal))
        {
            throw new SubscriptionValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["paymentOptionId"] = ["The payment option is not available."],
            });
        }
    }

    private void RequireKnownCatalogId(string? catalogId)
    {
        if (catalogId is not null && catalog.TryGet(catalogId) is null)
        {
            throw new SubscriptionValidationException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["catalogId"] = ["The catalog id is not a known service."],
            });
        }
    }

    /// <summary>One content-free line per Edit save, so an Edit-driven change shows apart from Correct and Add price.</summary>
    private void LogPaymentChange(DetailsUpdate command, IReadOnlyList<object> events)
    {
        if (loggers is null || command.PaymentOption is null)
        {
            return;
        }

        var priceEvents = events.Count(e => e is SubscriptionPriceEntryAdded or SubscriptionPriceEntryPaymentChanged);
        var change = priceEvents == 0 ? "none" : command.PaymentOption.PaymentOptionId is null ? "cleared" : "set";
        PaymentOptionLog.EditChange(loggers.CreateLogger(PaymentOptionLog.Category), change, priceEvents);
    }

    private static void SetHeaders(IDocumentSession session, WriteContext context)
    {
        session.SetHeader("actor", context.Actor);
        if (!string.IsNullOrEmpty(context.CorrelationId))
        {
            session.SetHeader("correlationId", context.CorrelationId);
        }
    }
}
