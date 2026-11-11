using Marten;
using Subscribed.Household.Domain;
using Subscribed.Platform.EventStore;

namespace Subscribed.Household;

/// <summary>Who caused a write; stored as event headers. Never contains claims beyond a member id.</summary>
public sealed record WriteContext(string Actor, string? CorrelationId)
{
    public const string AdminCli = "admin-cli";

    public static WriteContext Member(string memberId, string? correlationId) => new($"member:{memberId}", correlationId);
}

/// <summary>Household commands and queries (the module's only public surface besides endpoints).</summary>
public sealed class HouseholdService(IDocumentStore store, TimeProvider time)
{
    public const string StreamId = Domain.Household.DefaultStreamId;

    /// <summary>One uncached primary-key read of the inline view.</summary>
    public Task<HouseholdView?> GetViewAsync(CancellationToken ct = default) =>
        EventStoreErrors.GuardAsync(async () =>
        {
            await using var session = store.QuerySession();
            return await session.LoadAsync<HouseholdView>(StreamId, ct).ConfigureAwait(false);
        });

    /// <summary>Rehydrates the aggregate from its events (never from the view).</summary>
    public Task<Domain.Household> LoadAggregateAsync(CancellationToken ct = default) =>
        EventStoreErrors.GuardAsync(async () =>
        {
            await using var session = store.QuerySession();
            return await RehydrateAsync(session, ct).ConfigureAwait(false);
        });

    public Task<HouseholdView> InitializeAsync(string name, string ownerIssuer, string ownerSubject, string ownerLabel, WriteContext context, CancellationToken ct = default) =>
        WriteAsync(null, h =>
        {
            var created = h.Create(name, time.GetUtcNow());
            var probe = Domain.Household.Rehydrate(created);
            return [.. created, .. probe.Grant(ownerIssuer, ownerSubject, ownerLabel, time.GetUtcNow())];
        }, context, ct);

    public Task<HouseholdView> RenameAsync(long expectedVersion, string name, WriteContext context, CancellationToken ct = default) =>
        WriteAsync(expectedVersion, h => h.Rename(name, time.GetUtcNow()), context, ct);

    public Task<HouseholdView> GrantAsync(string issuer, string subject, string label, WriteContext context, CancellationToken ct = default) =>
        WriteAsync(null, h => h.Grant(issuer, subject, label, time.GetUtcNow()), context, ct);

    public Task<HouseholdView> RevokeAsync(string memberId, WriteContext context, CancellationToken ct = default) =>
        WriteAsync(null, h => h.Revoke(memberId, time.GetUtcNow()), context, ct);

    /// <summary>
    /// Load (optimistic, no lock) → decide → append at the loaded version → commit. The events, the
    /// stream version bump and the inline view upsert commit in one transaction.
    /// </summary>
    private Task<HouseholdView> WriteAsync(long? expectedVersion, Func<Domain.Household, IReadOnlyList<object>> decide, WriteContext context, CancellationToken ct) =>
        EventStoreErrors.GuardAsync(async () =>
        {
            await using var session = store.LightweightSession();
            var household = await RehydrateAsync(session, ct).ConfigureAwait(false);
            if (expectedVersion is { } expected && household.Version != expected)
            {
                throw new EventStoreException(new EventStoreFailure(EventStoreFailureKind.ConcurrencyConflict), new InvalidOperationException("Stale expected version."));
            }

            var events = decide(household);
            if (events.Count > 0)
            {
                session.SetHeader("actor", context.Actor);
                if (!string.IsNullOrEmpty(context.CorrelationId))
                {
                    session.SetHeader("correlationId", context.CorrelationId);
                }

                if (household.Exists)
                {
                    // Marten's expected version is the stream version AFTER the append.
                    session.Events.Append(StreamId, household.Version + events.Count, [.. events]);
                }
                else
                {
                    session.Events.StartStream<HouseholdView>(StreamId, [.. events]);
                }

                await session.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            return await session.LoadAsync<HouseholdView>(StreamId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Household view missing after write.");
        });

    private static async Task<Domain.Household> RehydrateAsync(IQuerySession session, CancellationToken ct)
    {
        var events = await session.Events.FetchStreamAsync(StreamId, token: ct).ConfigureAwait(false);
        var household = Domain.Household.Rehydrate(events.Select(e => e.Data));
        if (events.Count > 0 && events[^1].Version != household.Version)
        {
            throw new InvalidOperationException("Stream version does not match the number of applied events.");
        }

        return household;
    }
}

/// <summary>Deletes and reprojects the view documents from events in one transaction (migrator only).</summary>
public static class HouseholdProjectionRebuilder
{
    public static async Task<int> RebuildAsync(IDocumentStore migratorStore, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(migratorStore);
        await using var session = migratorStore.LightweightSession();
        var view = await session.Events.AggregateStreamAsync<HouseholdView>(HouseholdService.StreamId, token: ct).ConfigureAwait(false);
        session.DeleteWhere<HouseholdView>(_ => true);
        if (view is not null)
        {
            view.Id = HouseholdService.StreamId;
            session.Store(view);
        }

        await session.SaveChangesAsync(ct).ConfigureAwait(false);
        return view is null ? 0 : 1;
    }
}
