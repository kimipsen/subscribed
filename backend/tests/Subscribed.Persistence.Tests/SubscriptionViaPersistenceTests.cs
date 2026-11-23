using Subscribed.Household;
using Subscribed.Platform.EventStore;
using Subscribed.Subscriptions;
using Subscribed.Subscriptions.Domain;
using Subscribed.TestSupport;
using Xunit;

namespace Subscribed.Persistence.Tests;

/// <summary>#55 WP3: MCP-context writes store source/tokenId headers and stamp via; web writes store neither (D5, D8, D10).</summary>
public sealed class SubscriptionViaPersistenceTests(PostgresFixture pg)
{
    private static readonly IReadOnlyList<IEventStoreModule> Modules = [new HouseholdModule(), new SubscriptionsModule(), new Subscribed.Subscriptions.PaymentOptions.PaymentOptionsModule()];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WriteContext Web => WriteContext.Member("m1", "corr-web");

    private static WriteContext Mcp => WriteContext.Mcp("m1", "tok-1", "corr-mcp");

    private static NewSubscription Sub() =>
        SubscriptionDecisions.ParseAdd(new AddInput("StreamCo", null, null, "DKK", "2024-03-01", null, "129.00", "month", 1), TimeZoneInfo.Utc);

    private static PriceEntryChange Entry(string from, string amount) => SubscriptionDecisions.ParsePriceEntry(new PriceInput(from, amount, "month", 1));

    private static PauseChange Pause(string from, string? to) => SubscriptionDecisions.ParsePause(new PauseInput(from, to));

    private async Task<(Marten.DocumentStore Store, SubscriptionService Service, TestDatabase Db)> StartAsync()
    {
        var db = await pg.CreateDatabaseAsync();
        var store = SubscribedStore.Create(db.AppConnectionString, PostgresFixture.Schema, StoreRole.App, Modules);
        return (store, new SubscriptionService(store, TimeProvider.System, TestCatalog.Standard()), db);
    }

    private static string Key(SubscriptionState s) => SubscriptionIds.StreamKey(SubscriptionIds.Bare(s.Id));

    [Fact]
    public async Task An_mcp_write_stores_source_and_token_headers_and_a_web_write_stores_neither()
    {
        var (store, service, db) = await StartAsync();
        await using var _db = db;
        await using var _store = store;

        var added = await service.AddAsync(Sub(), Web, Ct);
        var bare = SubscriptionIds.Bare(added.Id);
        var viaWeb = await service.AddPriceEntryAsync(bare, added.Version, Entry("2025-01-01", "139.00"), Web, Ct);
        await service.AddPriceEntryAsync(bare, viaWeb.Version, Entry("2026-01-01", "149.00"), Mcp, Ct);

        var key = Key(added);
        var web = await db.ScalarAsMigratorAsync<string>($"SELECT headers::text FROM subscribed.mt_events WHERE stream_id = '{key}' AND version = 2");
        var mcp = await db.ScalarAsMigratorAsync<string>($"SELECT headers::text FROM subscribed.mt_events WHERE stream_id = '{key}' AND version = 3");
        Assert.DoesNotContain("source", web, StringComparison.Ordinal);
        Assert.DoesNotContain("tokenId", web, StringComparison.Ordinal);
        Assert.Contains("\"source\": \"mcp\"", mcp, StringComparison.Ordinal);
        Assert.Contains("\"tokenId\": \"tok-1\"", mcp, StringComparison.Ordinal);
        Assert.Contains("member:m1", mcp, StringComparison.Ordinal);
        Assert.Contains("corr-mcp", mcp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_first_price_row_of_an_mcp_add_shows_via_and_a_web_add_does_not()
    {
        var (store, service, db) = await StartAsync();
        await using var _db = db;
        await using var _store = store;

        var viaMcp = await service.AddAsync(Sub(), Mcp, Ct);
        var viaWeb = await service.AddAsync(Sub(), Web, Ct);

        Assert.Equal("mcp", Assert.Single(viaMcp.Entries).Via);
        Assert.Null(Assert.Single(viaWeb.Entries).Via);
        var stored = await db.ScalarAsMigratorAsync<string>($"SELECT data->>'via' FROM subscribed.mt_events WHERE stream_id = '{Key(viaMcp)}' AND version = 1");
        Assert.Equal("mcp", stored);
    }

    [Fact]
    public async Task D10_price_entry_via_follows_the_latest_writer_in_both_directions()
    {
        var (store, service, db) = await StartAsync();
        await using var _db = db;
        await using var _store = store;

        var added = await service.AddAsync(Sub(), Web, Ct);
        var bare = SubscriptionIds.Bare(added.Id);
        var withEntry = await service.AddPriceEntryAsync(bare, added.Version, Entry("2025-01-01", "139.00"), Mcp, Ct);
        var entryId = withEntry.Entries.Single(e => e.Via == "mcp").EntryId;

        var webEdit = await service.CorrectPriceEntryAsync(bare, withEntry.Version, entryId, Entry("2025-01-01", "140.00"), Web, Ct);
        Assert.Null(webEdit.Entries.Single(e => e.EntryId == entryId).Via);

        var mcpEdit = await service.CorrectPriceEntryAsync(bare, webEdit.Version, entryId, Entry("2025-01-01", "141.00"), Mcp, Ct);
        Assert.Equal("mcp", mcpEdit.Entries.Single(e => e.EntryId == entryId).Via);
        Assert.Null(mcpEdit.Entries.Single(e => e.EntryId == added.InitialEntryId).Via);
    }

    [Fact]
    public async Task D10_an_mcp_resume_of_a_web_pause_sets_via_and_a_web_edit_of_an_mcp_period_clears_it()
    {
        var (store, service, db) = await StartAsync();
        await using var _db = db;
        await using var _store = store;
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero));
        var timed = new SubscriptionService(store, clock, TestCatalog.Standard());
        var zone = TimeZoneInfo.Utc;

        var added = await timed.AddAsync(Sub(), Web, Ct);
        var bare = SubscriptionIds.Bare(added.Id);
        var paused = await timed.AddPausePeriodAsync(bare, added.Version, Pause("2026-11-01", null), zone, Web, Ct);
        Assert.Null(Assert.Single(paused.PausePeriods).Via);

        var resumed = await timed.ResumeAsync(bare, paused.Version, zone, Mcp, Ct);
        var period = Assert.Single(resumed.PausePeriods);
        Assert.Equal("mcp", period.Via);

        var edited = await timed.ChangePausePeriodAsync(bare, resumed.Version, period.PeriodId, Pause("2026-11-02", "2026-11-13"), zone, Web, Ct);
        Assert.Null(Assert.Single(edited.PausePeriods).Via);

        var mcpAdded = await timed.AddPausePeriodAsync(bare, edited.Version, Pause("2026-10-01", "2026-10-05"), zone, Mcp, Ct);
        Assert.Equal(["mcp"], mcpAdded.PausePeriods.Where(p => p.Via is not null).Select(p => p.Via).ToArray());
        _ = service;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
