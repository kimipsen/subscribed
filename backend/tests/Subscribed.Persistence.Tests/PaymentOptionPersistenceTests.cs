using System.Text.Json;
using Marten;
using Subscribed.Household;
using Subscribed.Platform.Database;
using Subscribed.Platform.EventStore;
using Subscribed.Subscriptions;
using Subscribed.Subscriptions.Domain;
using Subscribed.Subscriptions.PaymentOptions;
using Subscribed.TestSupport;
using Xunit;

namespace Subscribed.Persistence.Tests;

/// <summary>Payment option streams and the subscription link, as the least-privilege app role.</summary>
public sealed class PaymentOptionPersistenceTests(PostgresFixture pg)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WriteContext Member => new("member:test-member", "corr-456");

    private static DocumentStore AppStore(TestDatabase db) => SubscribedStore.Create(db.AppConnectionString, PostgresFixture.Schema, StoreRole.App, PostgresFixture.Modules);

    private static DocumentStore MigratorStore(TestDatabase db) => SubscribedStore.Create(db.MigratorConnectionString, PostgresFixture.Schema, StoreRole.Migrator, PostgresFixture.Modules);

    private static NewSubscription Sub(string? optionId = null, string name = "StreamCo") =>
        SubscriptionDecisions.ParseAdd(new AddInput(name, null, null, "DKK", "2024-03-01", null, "129.00", "month", 1, null, optionId));

    private static PriceEntryChange Entry(string from, string amount, string? optionId) =>
        SubscriptionDecisions.ParsePriceEntry(new PriceInput(from, amount, "month", 1, optionId));

    private static PaymentOptionFields Fields(string name, string? label = null) => PaymentOptionDecisions.ParseCreate(new PaymentOptionInput(name, label));

    [Fact]
    public async Task Create_change_and_remove_store_v1_aliases_headers_and_follow_the_inline_snapshot()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var service = new PaymentOptionService(store, TimeProvider.System);

        var created = await service.CreateAsync(Fields("Visa", "Joint"), Member, Ct);
        var id = PaymentOptionIds.Bare(created.Id);
        var renamed = await service.UpdateAsync(id, created.Version, Fields("Visa Gold", "Joint"), Member, Ct);
        var removed = await service.RemoveAsync(id, renamed.Version, Member, Ct);

        Assert.Equal(3, removed.Version);
        Assert.True(removed.Removed);
        Assert.Equal("Visa Gold", removed.Name);
        var key = PaymentOptionIds.StreamKey(id);
        Assert.Equal(
            "payment_option.created.v1,payment_option.changed.v1,payment_option.removed.v1",
            await db.ScalarAsMigratorAsync<string>($"SELECT string_agg(type, ',' ORDER BY version) FROM subscribed.mt_events WHERE stream_id = '{key}'"));
        var headers = await db.ScalarAsMigratorAsync<string>($"SELECT headers::text FROM subscribed.mt_events WHERE stream_id = '{key}' AND version = 2");
        Assert.Contains("member:test-member", headers, StringComparison.Ordinal);
        Assert.Contains("corr-456", headers, StringComparison.Ordinal);
        var data = JsonElement.Parse((await db.ScalarAsMigratorAsync<string>($"SELECT data::text FROM subscribed.mt_events WHERE stream_id = '{key}' AND version = 1"))!);
        Assert.Equal(HouseholdService.StreamId, data.GetProperty("householdId").GetString());

        // A removed option stays in the list and keeps resolving by id (D3).
        var listed = Assert.Single(await service.ListAsync(Ct));
        Assert.True(listed.Removed);
        Assert.NotNull(await service.GetAsync(id, Ct));
        await Assert.ThrowsAsync<PaymentOptionNotFoundException>(() => service.UpdateAsync(id, removed.Version, Fields("Again"), Member, Ct));
        await Assert.ThrowsAsync<PaymentOptionNotFoundException>(() => service.RemoveAsync(id, removed.Version, Member, Ct));
    }

    [Fact]
    public async Task A_stale_version_is_a_concurrency_conflict()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var service = new PaymentOptionService(store, TimeProvider.System);
        var created = await service.CreateAsync(Fields("Visa"), Member, Ct);
        await service.UpdateAsync(PaymentOptionIds.Bare(created.Id), created.Version, Fields("Visa 2"), Member, Ct);
        var ex = await Assert.ThrowsAsync<EventStoreException>(() => service.UpdateAsync(PaymentOptionIds.Bare(created.Id), created.Version, Fields("Visa 3"), Member, Ct));
        Assert.Equal(EventStoreFailureKind.ConcurrencyConflict, ex.Failure.Kind);
    }

    [Fact]
    public async Task An_option_of_another_household_is_invisible()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var service = new PaymentOptionService(store, TimeProvider.System);
        var foreignId = await StartForeignOptionAsync(store);

        Assert.Empty(await service.ListAsync(Ct));
        Assert.Null(await service.GetAsync(foreignId, Ct));
        await Assert.ThrowsAsync<PaymentOptionNotFoundException>(() => service.UpdateAsync(foreignId, 1, Fields("Mine now"), Member, Ct));
        await Assert.ThrowsAsync<PaymentOptionNotFoundException>(() => service.RemoveAsync(foreignId, 1, Member, Ct));
    }

    [Fact]
    public async Task Assigning_a_removed_unknown_or_foreign_option_is_rejected_and_an_unchanged_removed_reference_stays_editable()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var options = new PaymentOptionService(store, TimeProvider.System);
        var subs = new SubscriptionService(store, TimeProvider.System, TestCatalog.Standard());
        var live = await options.CreateAsync(Fields("Visa"), Member, Ct);
        var liveId = PaymentOptionIds.Bare(live.Id);
        var foreignId = await StartForeignOptionAsync(store);
        var unknownId = PaymentOptionIds.New(TimeProvider.System);

        foreach (var bad in new[] { unknownId, foreignId })
        {
            var ex = await Assert.ThrowsAsync<SubscriptionValidationException>(() => subs.AddAsync(Sub(bad), Member, Ct));
            Assert.Equal("paymentOptionId", Assert.Single(ex.Errors).Key);
        }

        Assert.Empty(await subs.ListAsync(Ct));

        var added = await subs.AddAsync(Sub(liveId), Member, Ct);
        var subId = SubscriptionIds.Bare(added.Id);
        Assert.Equal(liveId, added.Entries.Single().PaymentOptionId);
        Assert.Equal(2, added.Version); // added + payment_changed

        await options.RemoveAsync(liveId, live.Version, Member, Ct);

        // The race is accepted (D15): the entry still shows the id, which resolves as removed.
        Assert.Equal(liveId, (await subs.GetAsync(subId, Ct))!.Entries.Single().PaymentOptionId);
        Assert.True((await options.GetAsync(liveId, Ct))!.Removed);

        // A new assignment of the removed option is rejected, but an unchanged reference never is.
        var entryId = added.Entries.Single().EntryId;
        var unchanged = await subs.CorrectPriceEntryAsync(subId, added.Version, entryId, Entry("2024-03-01", "139.00", liveId), Member, Ct);
        Assert.Equal(liveId, unchanged.Entries.Single().PaymentOptionId);
        await Assert.ThrowsAsync<SubscriptionValidationException>(() => subs.AddPriceEntryAsync(subId, unchanged.Version, Entry("2025-01-01", "149.00", liveId), Member, Ct));
        await Assert.ThrowsAsync<SubscriptionValidationException>(() => subs.AddPriceEntryAsync(subId, unchanged.Version, Entry("2025-01-01", "149.00", unknownId), Member, Ct));
    }

    [Fact]
    public async Task The_async_decide_keeps_the_404_then_412_then_400_order()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var subs = new SubscriptionService(store, TimeProvider.System, TestCatalog.Standard());
        var unknownOption = PaymentOptionIds.New(TimeProvider.System);
        var added = await subs.AddAsync(Sub(), Member, Ct);
        var subId = SubscriptionIds.Bare(added.Id);

        await Assert.ThrowsAsync<SubscriptionNotFoundException>(() => subs.AddPriceEntryAsync(SubscriptionIds.New(TimeProvider.System), 1, Entry("2025-01-01", "1.00", unknownOption), Member, Ct));
        var stale = await Assert.ThrowsAsync<EventStoreException>(() => subs.AddPriceEntryAsync(subId, added.Version + 5, Entry("2025-01-01", "1.00", unknownOption), Member, Ct));
        Assert.Equal(EventStoreFailureKind.ConcurrencyConflict, stale.Failure.Kind);
        await Assert.ThrowsAsync<SubscriptionValidationException>(() => subs.AddPriceEntryAsync(subId, added.Version, Entry("2025-01-01", "1.00", unknownOption), Member, Ct));
        Assert.Equal(added.Version, (await subs.GetAsync(subId, Ct))!.Version);
    }

    [Fact]
    public async Task A_price_and_option_change_raises_the_stream_version_by_two_in_one_append()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var options = new PaymentOptionService(store, TimeProvider.System);
        var subs = new SubscriptionService(store, TimeProvider.System, TestCatalog.Standard());
        var a = PaymentOptionIds.Bare((await options.CreateAsync(Fields("Visa"), Member, Ct)).Id);
        var added = await subs.AddAsync(Sub(), Member, Ct);
        var entryId = added.Entries.Single().EntryId;

        var changed = await subs.CorrectPriceEntryAsync(SubscriptionIds.Bare(added.Id), added.Version, entryId, Entry("2024-03-01", "139.00", a), Member, Ct);
        Assert.Equal(added.Version + 2, changed.Version);
        Assert.Equal(
            "subscription.added.v1,subscription.price_entry_corrected.v1,subscription.price_entry_payment_changed.v1",
            await db.ScalarAsMigratorAsync<string>($"SELECT string_agg(type, ',' ORDER BY version) FROM subscribed.mt_events WHERE stream_id = '{StreamKey(added)}'"));
    }

    [Fact]
    public async Task Rebuilders_reproduce_both_inline_snapshots_and_leave_a_stream_without_payment_events_unchanged()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var options = new PaymentOptionService(store, TimeProvider.System);
        var subs = new SubscriptionService(store, TimeProvider.System, TestCatalog.Standard());
        var a = await options.CreateAsync(Fields("Visa", "Joint"), Member, Ct);
        var b = await options.CreateAsync(Fields("PayPal"), Member, Ct);
        await options.UpdateAsync(PaymentOptionIds.Bare(a.Id), a.Version, Fields("Visa Gold", "Joint"), Member, Ct);
        await options.RemoveAsync(PaymentOptionIds.Bare(b.Id), b.Version, Member, Ct);
        var withOption = await subs.AddAsync(Sub(PaymentOptionIds.Bare(a.Id)), Member, Ct);
        await subs.AddPriceEntryAsync(SubscriptionIds.Bare(withOption.Id), withOption.Version, Entry("2025-01-01", "149.00", PaymentOptionIds.Bare(a.Id)), Member, Ct);
        await subs.AddAsync(Sub(null, "Plain"), Member, Ct);

        const string subSql = "SELECT string_agg(id || ':' || data::text, '|' ORDER BY id) FROM subscribed.mt_doc_subscription_view";
        const string optSql = "SELECT string_agg(id || ':' || data::text, '|' ORDER BY id) FROM subscribed.mt_doc_payment_option_view";
        var subBefore = await db.ScalarAsMigratorAsync<string>(subSql);
        var optBefore = await db.ScalarAsMigratorAsync<string>(optSql);
        await db.ExecuteAsMigratorAsync("DELETE FROM subscribed.mt_doc_subscription_view; DELETE FROM subscribed.mt_doc_payment_option_view");
        Assert.Empty(await options.ListAsync(Ct));

        await using (var migrator = MigratorStore(db))
        {
            Assert.Equal(2, await SubscriptionProjectionRebuilder.RebuildAsync(migrator, Ct));
            Assert.Equal(2, await PaymentOptionProjectionRebuilder.RebuildAsync(migrator, Ct));
        }

        Assert.Equal(subBefore, await db.ScalarAsMigratorAsync<string>(subSql));
        Assert.Equal(optBefore, await db.ScalarAsMigratorAsync<string>(optSql));
    }

    [Fact]
    public async Task A_snapshot_stored_before_the_feature_loads_with_no_option_and_can_then_be_assigned_one()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var options = new PaymentOptionService(store, TimeProvider.System);
        var subs = new SubscriptionService(store, TimeProvider.System, TestCatalog.Standard());
        var added = await subs.AddAsync(Sub(), Member, Ct);

        // Rows written before this feature have no paymentOptionId on any entry.
        await db.ExecuteAsMigratorAsync(
            "UPDATE subscribed.mt_doc_subscription_view SET data = jsonb_set(data, '{entries}', (SELECT jsonb_agg(e - 'paymentOptionId') FROM jsonb_array_elements(data->'entries') e))");
        Assert.Equal(0, await db.ScalarAsMigratorAsync<long>("SELECT count(*) FROM subscribed.mt_doc_subscription_view, jsonb_array_elements(data->'entries') e WHERE e ? 'paymentOptionId'"));

        var loaded = (await subs.GetAsync(SubscriptionIds.Bare(added.Id), Ct))!;
        Assert.Null(loaded.Entries.Single().PaymentOptionId);
        var option = await options.CreateAsync(Fields("Visa"), Member, Ct);
        var assigned = await subs.CorrectPriceEntryAsync(SubscriptionIds.Bare(added.Id), loaded.Version, loaded.Entries.Single().EntryId, Entry("2024-03-01", "129.00", PaymentOptionIds.Bare(option.Id)), Member, Ct);
        Assert.Equal(PaymentOptionIds.Bare(option.Id), assigned.Entries.Single().PaymentOptionId);
    }

    [Fact]
    public async Task An_unknown_alias_in_a_payment_option_stream_fails_loudly()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var service = new PaymentOptionService(store, TimeProvider.System);
        var created = await service.CreateAsync(Fields("Visa"), Member, Ct);
        var key = created.Id;
        const string empty = "{}";
        await db.ExecuteAsMigratorAsync(
            $"""
            UPDATE subscribed.mt_streams SET version = 2 WHERE id = '{key}';
            INSERT INTO subscribed.mt_events (seq_id, id, stream_id, version, data, type, timestamp, tenant_id, mt_dotnet_type, is_archived)
            VALUES (nextval('subscribed.mt_events_sequence'), gen_random_uuid(), '{key}', 2, '{empty}'::jsonb, 'payment_option.bogus.v1', now(), '*DEFAULT*', 'Bogus.Type, Bogus', false);
            """);
        await Assert.ThrowsAsync<Marten.Exceptions.UnknownEventTypeException>(() => service.UpdateAsync(PaymentOptionIds.Bare(created.Id), 2, Fields("x"), Member, Ct));
    }

    private static string StreamKey(SubscriptionState s) => SubscriptionIds.StreamKey(SubscriptionIds.Bare(s.Id));

    /// <summary>Starts an option stream of a household that is not the app's single household.</summary>
    internal static async Task<string> StartForeignOptionAsync(IDocumentStore store)
    {
        var id = PaymentOptionIds.New(TimeProvider.System);
        await using var session = store.LightweightSession();
        session.Events.StartStream<PaymentOptionState>(PaymentOptionIds.StreamKey(id), new PaymentOptionCreated("household-other", "Foreign", null, DateTimeOffset.UtcNow));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        return id;
    }
}
