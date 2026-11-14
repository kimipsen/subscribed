using System.Globalization;
using System.Text.Json;
using Marten;
using Subscribed.Household;
using Subscribed.Platform.Database;
using Subscribed.Platform.EventStore;
using Subscribed.Subscriptions;
using Subscribed.Subscriptions.Domain;
using Subscribed.TestSupport;
using Xunit;

namespace Subscribed.Persistence.Tests;

/// <summary>Runs as the least-privilege app role, like <see cref="EventStoreTests"/>.</summary>
public sealed class SubscriptionPersistenceTests(PostgresFixture pg)
{
    private static readonly IReadOnlyList<IEventStoreModule> Modules = [new HouseholdModule(), new SubscriptionsModule()];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WriteContext Member => new("member:test-member", "corr-123");

    private static DocumentStore AppStore(TestDatabase db) => SubscribedStore.Create(db.AppConnectionString, PostgresFixture.Schema, StoreRole.App, Modules);

    private static DocumentStore MigratorStore(TestDatabase db) => SubscribedStore.Create(db.MigratorConnectionString, PostgresFixture.Schema, StoreRole.Migrator, Modules);

    private sealed class TestClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private static NewSubscription Sub(string name = "StreamCo", string currency = "DKK", string amount = "129.00", string start = "2024-03-01") =>
        SubscriptionDecisions.ParseAdd(new AddInput(name, null, null, currency, start, null, amount, "month", 1));

    private static PriceEntryChange Entry(string from, string amount, string unit = "month", int count = 1) =>
        SubscriptionDecisions.ParsePriceEntry(new PriceInput(from, amount, unit, count));

    private static string StreamKey(SubscriptionState s) => SubscriptionIds.StreamKey(SubscriptionIds.Bare(s.Id));

    private static async Task<List<object>> HistoryAsync(DocumentStore store, string id)
    {
        await using var session = store.QuerySession();
        var events = await session.Events.FetchStreamAsync(SubscriptionIds.StreamKey(id), token: Ct);
        return [.. events.Select(e => e.Data)];
    }

    [Fact]
    public async Task Add_stores_v1_aliases_headers_and_an_exact_amount()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var service = new SubscriptionService(store, TimeProvider.System);

        var added = await service.AddAsync(Sub(amount: "11.99", currency: "EUR"), Member, Ct);
        await service.AddPriceEntryAsync(SubscriptionIds.Bare(added.Id), added.Version, Entry("2025-01-01", "12.99"), Member, Ct);

        var key = StreamKey(added);
        var types = await db.ScalarAsMigratorAsync<string>($"SELECT string_agg(type, ',' ORDER BY version) FROM subscribed.mt_events WHERE stream_id = '{key}'");
        Assert.Equal("subscription.added.v1,subscription.price_entry_added.v1", types);
        Assert.Equal("11.99", await db.ScalarAsMigratorAsync<string>($"SELECT data->>'amount' FROM subscribed.mt_events WHERE stream_id = '{key}' AND version = 1"));
        var headers = await db.ScalarAsMigratorAsync<string>($"SELECT headers::text FROM subscribed.mt_events WHERE stream_id = '{key}' AND version = 2");
        Assert.Contains("member:test-member", headers, StringComparison.Ordinal);
        Assert.Contains("corr-123", headers, StringComparison.Ordinal);
        var data = await db.ScalarAsMigratorAsync<string>($"SELECT data::text FROM subscribed.mt_events WHERE stream_id = '{key}' AND version = 1");
        Assert.DoesNotContain("email", data, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", data, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HouseholdService.StreamId, JsonElement.Parse(data!).GetProperty("householdId").GetString());
    }

    [Fact]
    public async Task Backdated_entries_keep_recorded_at_apart_from_effective_from()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var clock = new TestClock(DateTimeOffset.Parse("2026-11-13T09:30:00Z", CultureInfo.InvariantCulture));
        var service = new SubscriptionService(store, clock);

        var added = await service.AddAsync(Sub(start: "2021-01-01"), Member, Ct);
        await service.AddPriceEntryAsync(SubscriptionIds.Bare(added.Id), added.Version, Entry("2022-06-01", "69.00"), Member, Ct);

        var data = await db.ScalarAsMigratorAsync<string>($"SELECT data::text FROM subscribed.mt_events WHERE stream_id = '{StreamKey(added)}' AND version = 2");
        var root = JsonElement.Parse(data!);
        Assert.Equal("2022-06-01", root.GetProperty("effectiveFrom").GetString());
        Assert.StartsWith("2026-11-13T09:30:00", root.GetProperty("recordedAt").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_document_equals_the_rehydrated_state_and_the_versions_agree()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var clock = new TestClock(DateTimeOffset.Parse("2026-11-13T09:30:00Z", CultureInfo.InvariantCulture));
        var service = new SubscriptionService(store, clock);

        var s = await service.AddAsync(Sub("FilmPlus", amount: "59.00", start: "2021-01-01"), Member, Ct);
        s = await service.AddPriceEntryAsync(SubscriptionIds.Bare(s.Id), s.Version, Entry("2022-06-01", "69.00"), Member, Ct);
        var second = s.Entries.Single(e => e.EffectiveFrom == new DateOnly(2022, 6, 1));
        s = await service.AddPriceEntryAsync(SubscriptionIds.Bare(s.Id), s.Version, Entry("2023-11-01", "79.00"), Member, Ct);
        var third = s.Entries.Single(e => e.EffectiveFrom == new DateOnly(2023, 11, 1));
        s = await service.CorrectPriceEntryAsync(SubscriptionIds.Bare(s.Id), s.Version, second.EntryId, Entry("2022-06-01", "65.00"), Member, Ct);
        s = await service.UpdateAsync(SubscriptionIds.Bare(s.Id), s.Version, new DetailsUpdate("FilmPlus HD", "Streaming", "Family\nplan", "DKK", new DateOnly(2020, 12, 1), null), Member, Ct);
        s = await service.RemovePriceEntryAsync(SubscriptionIds.Bare(s.Id), s.Version, third.EntryId, Member, Ct);

        var history = await HistoryAsync(store, SubscriptionIds.Bare(s.Id));
        var rehydrated = SubscriptionState.Rehydrate(history);
        rehydrated.Id = s.Id;

        Assert.Equal(7, history.Count);
        Assert.Equal(history.Count, rehydrated.Version);
        Assert.Equal(rehydrated.Version, s.Version);
        Assert.Equal(await db.ScalarAsMigratorAsync<long>($"SELECT version FROM subscribed.mt_streams WHERE id = '{StreamKey(s)}'"), s.Version);
        Assert.Equal(JsonSerializer.Serialize(rehydrated), JsonSerializer.Serialize(s));
        Assert.Equal(new DateOnly(2020, 12, 1), s.Entries.Single(e => e.EntryId == s.InitialEntryId).EffectiveFrom);
        Assert.Equal(65.00m, s.Entries.Single(e => e.EntryId == second.EntryId).Amount);
        Assert.DoesNotContain(s.Entries, e => e.EntryId == third.EntryId);
    }

    [Fact]
    public async Task An_unchanged_update_returns_the_same_version_and_appends_nothing()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var service = new SubscriptionService(store, TimeProvider.System);
        var added = await service.AddAsync(Sub(), Member, Ct);

        var same = await service.UpdateAsync(SubscriptionIds.Bare(added.Id), added.Version, new DetailsUpdate("StreamCo", null, null, "DKK", new DateOnly(2024, 3, 1), null), Member, Ct);

        Assert.Equal(added.Version, same.Version);
        Assert.Equal(1, await db.ScalarAsMigratorAsync<long>($"SELECT count(*) FROM subscribed.mt_events WHERE stream_id = '{StreamKey(added)}'"));
    }

    [Fact]
    public async Task List_and_get_hide_removed_subscriptions_but_keep_document_and_event()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var service = new SubscriptionService(store, TimeProvider.System);
        var keep = await service.AddAsync(Sub("Keep"), Member, Ct);
        var gone = await service.AddAsync(Sub("Gone"), Member, Ct);

        await service.RemoveAsync(SubscriptionIds.Bare(gone.Id), gone.Version, Member, Ct);

        Assert.Equal([keep.Id], (await service.ListAsync(Ct)).Select(x => x.Id));
        Assert.Null(await service.GetAsync(SubscriptionIds.Bare(gone.Id), Ct));
        Assert.NotNull(await service.GetAsync(SubscriptionIds.Bare(keep.Id), Ct));
        var key = StreamKey(gone);
        Assert.Equal("true", await db.ScalarAsMigratorAsync<string>($"SELECT data->>'removed' FROM subscribed.mt_doc_subscription_view WHERE id = '{key}'"));
        Assert.Equal(1, await db.ScalarAsMigratorAsync<long>($"SELECT count(*) FROM subscribed.mt_events WHERE stream_id = '{key}' AND type = 'subscription.removed.v1'"));
        await Assert.ThrowsAsync<SubscriptionNotFoundException>(() => service.RemoveAsync(SubscriptionIds.Bare(gone.Id), gone.Version + 1, Member, Ct));
        await Assert.ThrowsAsync<SubscriptionNotFoundException>(() => service.UpdateAsync("0".PadLeft(32, '0'), 1, new DetailsUpdate("x", null, null, "DKK", new DateOnly(2024, 1, 1), null), Member, Ct));
    }

    [Fact]
    public async Task Two_appends_at_the_same_version_produce_one_conflict()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var service = new SubscriptionService(store, TimeProvider.System);
        var added = await service.AddAsync(Sub(), Member, Ct);
        var key = StreamKey(added);

        await using var s1 = store.LightweightSession();
        await using var s2 = store.LightweightSession();
        s1.Events.Append(key, 2, new SubscriptionDetailsChanged("One", null, null, DateTimeOffset.UtcNow));
        s2.Events.Append(key, 2, new SubscriptionDetailsChanged("Two", null, null, DateTimeOffset.UtcNow));
        var results = await Task.WhenAll(Capture(s1.SaveChangesAsync(Ct)), Capture(s2.SaveChangesAsync(Ct)));

        var failures = results.Where(e => e is not null).ToList();
        Assert.Single(failures);
        Assert.Equal(EventStoreFailureKind.ConcurrencyConflict, EventStoreErrors.Map(failures[0]!)?.Kind);
        Assert.Equal(2, await db.ScalarAsMigratorAsync<long>($"SELECT count(*) FROM subscribed.mt_events WHERE stream_id = '{key}'"));
    }

    [Fact]
    public async Task A_stale_expected_version_is_a_concurrency_conflict_through_the_service()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var service = new SubscriptionService(store, TimeProvider.System);
        var added = await service.AddAsync(Sub(), Member, Ct);
        await service.UpdateAsync(SubscriptionIds.Bare(added.Id), added.Version, new DetailsUpdate("First", null, null, "DKK", new DateOnly(2024, 3, 1), null), Member, Ct);

        var ex = await Assert.ThrowsAsync<EventStoreException>(() =>
            service.UpdateAsync(SubscriptionIds.Bare(added.Id), added.Version, new DetailsUpdate("Stale", null, null, "DKK", new DateOnly(2024, 3, 1), null), Member, Ct));

        Assert.Equal(EventStoreFailureKind.ConcurrencyConflict, ex.Failure.Kind);
        Assert.Equal("First", (await service.GetAsync(SubscriptionIds.Bare(added.Id), Ct))!.Name);
    }

    [Fact]
    public async Task Creating_the_same_stream_concurrently_is_a_conflict()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var service = new SubscriptionService(store, TimeProvider.System);
        var added = await service.AddAsync(Sub(), Member, Ct);

        await using var session = store.LightweightSession();
        session.Events.StartStream<SubscriptionState>(StreamKey(added), SubscriptionDecisions.Add(Sub("Dup"), HouseholdService.StreamId, "e1", DateTimeOffset.UtcNow).ToArray());
        var raw = await Assert.ThrowsAsync<Marten.Exceptions.ExistingStreamIdCollisionException>(() => session.SaveChangesAsync(Ct));
        Assert.Equal(EventStoreFailureKind.ConcurrencyConflict, EventStoreErrors.Map(raw)?.Kind);
        Assert.Equal(1, await db.ScalarAsMigratorAsync<long>($"SELECT count(*) FROM subscribed.mt_events WHERE stream_id = '{StreamKey(added)}'"));
    }

    [Fact]
    public async Task Rebuild_as_the_migrator_restores_identical_documents_including_removed_ones()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var service = new SubscriptionService(store, TimeProvider.System);
        var active = await service.AddAsync(Sub("Active"), Member, Ct);
        active = await service.AddPriceEntryAsync(SubscriptionIds.Bare(active.Id), active.Version, Entry("2025-01-01", "139.00"), Member, Ct);
        var removed = await service.AddAsync(Sub("Removed", "EUR", "5.00"), Member, Ct);
        await service.RemoveAsync(SubscriptionIds.Bare(removed.Id), removed.Version, Member, Ct);

        const string sql = "SELECT string_agg(id || ':' || data::text, '|' ORDER BY id) FROM subscribed.mt_doc_subscription_view";
        var before = await db.ScalarAsMigratorAsync<string>(sql);
        await db.ExecuteAsMigratorAsync("DELETE FROM subscribed.mt_doc_subscription_view");
        Assert.Empty(await service.ListAsync(Ct));

        await using (var migrator = MigratorStore(db))
        {
            Assert.Equal(2, await SubscriptionProjectionRebuilder.RebuildAsync(migrator, Ct));
        }

        Assert.Equal(before, await db.ScalarAsMigratorAsync<string>(sql));
        Assert.Equal(active.Version, (await service.GetAsync(SubscriptionIds.Bare(active.Id), Ct))!.Version);
    }

    [Fact]
    public async Task Readiness_passes_with_the_new_grant_and_names_it_when_it_is_missing()
    {
        await using var db = await pg.CreateDatabaseAsync();
        var ready = await ReadinessProbe.CheckAsync(db.AppConnectionString, PostgresFixture.Schema, Ct);
        Assert.True(ready.Database && ready.Schema && ready.Privileges, string.Join(", ", ready.Problems));

        await db.ExecuteAsMigratorAsync($"REVOKE UPDATE ON subscribed.mt_doc_subscription_view FROM {pg.AppRole}");
        var missing = await ReadinessProbe.CheckAsync(db.AppConnectionString, PostgresFixture.Schema, Ct);
        Assert.False(missing.Privileges);
        Assert.Contains("privilege-missing:UPDATE:subscribed.mt_doc_subscription_view", missing.Problems);
    }

    [Theory]
    [InlineData("DELETE FROM subscribed.mt_doc_subscription_view")]
    [InlineData("TRUNCATE subscribed.mt_doc_subscription_view")]
    public async Task AC20_the_app_role_cannot_delete_subscription_documents(string sql)
    {
        await using var db = await pg.CreateDatabaseAsync();
        var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.ExecuteAsAppAsync(sql));
        Assert.Equal(Npgsql.PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task Migration_is_idempotent_at_schema_version_2_with_no_drift_for_either_module()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await SchemaMigrator.MigrateAsync(db.MigratorConnectionString, PostgresFixture.Schema, pg.AppRole, Modules, Ct);
        await using var migrator = MigratorStore(db);
        await migrator.Storage.Database.AssertDatabaseMatchesConfigurationAsync(Ct);

        Assert.Equal(2, SchemaVersion.Current);
        Assert.Equal(2, await db.ScalarAsMigratorAsync<int>("SELECT max(version) FROM subscribed.schema_migrations"));
        Assert.Equal(1, await db.ScalarAsMigratorAsync<long>("SELECT count(*) FROM pg_tables WHERE schemaname = 'subscribed' AND tablename = 'mt_doc_subscription_view'"));
    }

    [Fact]
    public async Task An_unknown_alias_in_a_subscription_stream_fails_loudly()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var service = new SubscriptionService(store, TimeProvider.System);
        var added = await service.AddAsync(Sub(), Member, Ct);
        var key = StreamKey(added);
        const string empty = "{}";
        await db.ExecuteAsMigratorAsync(
            $"""
            UPDATE subscribed.mt_streams SET version = 2 WHERE id = '{key}';
            INSERT INTO subscribed.mt_events (seq_id, id, stream_id, version, data, type, timestamp, tenant_id, mt_dotnet_type, is_archived)
            VALUES (nextval('subscribed.mt_events_sequence'), gen_random_uuid(), '{key}', 2, '{empty}'::jsonb, 'subscription.bogus.v1', now(), '*DEFAULT*', 'Bogus.Type, Bogus', false);
            """);

        // Marten refuses the stream before any subscription code runs: stored events are never skipped. The registry's own
        // UnknownSubscriptionEventAliasException is covered by the unit tests.
        await Assert.ThrowsAsync<Marten.Exceptions.UnknownEventTypeException>(() =>
            service.UpdateAsync(SubscriptionIds.Bare(added.Id), 2, new DetailsUpdate("x", null, null, "DKK", new DateOnly(2024, 3, 1), null), Member, Ct));
    }

    private static async Task<Exception?> Capture(Task task)
    {
        try
        {
            await task;
            return null;
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return ex;
        }
    }
}
