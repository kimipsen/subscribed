using Marten;
using Microsoft.Extensions.Logging;
using Subscribed.Household;
using Subscribed.Household.Domain;
using Subscribed.Platform.EventStore;
using Subscribed.Subscriptions;
using Subscribed.Subscriptions.PaymentOptions;
using Subscribed.TestSupport;
using Xunit;

namespace Subscribed.Persistence.Tests;

/// <summary>Household time zone and calendar feed rows: events, inline view, rebuild parity, legacy rows and log lines (#33).</summary>
public sealed class HouseholdCalendarFeedPersistenceTests(PostgresFixture pg)
{
    private const string Issuer = "https://idp.example.invalid/realms/synthetic";
    private const string ViewSql = "SELECT data::text FROM subscribed.mt_doc_household_view WHERE id = 'household-default'";
    private static readonly IReadOnlyList<IEventStoreModule> Modules = [new HouseholdModule(), new SubscriptionsModule(), new PaymentOptionsModule()];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WriteContext Admin => new(WriteContext.AdminCli, "00-test");

    private static DocumentStore AppStore(TestDatabase db) => SubscribedStore.Create(db.AppConnectionString, PostgresFixture.Schema, StoreRole.App, Modules);

    private static DocumentStore MigratorStore(TestDatabase db) => SubscribedStore.Create(db.MigratorConnectionString, PostgresFixture.Schema, StoreRole.Migrator, Modules);

    private static Task<HouseholdView> InitAsync(HouseholdService service) =>
        service.InitializeAsync("Synthetic Household", Issuer, "subject-a", "Member A", Admin, Ct);

    private static HouseholdService ServiceWithLogs(IDocumentStore store, LogCapture logs) =>
        new(store, TimeProvider.System, LoggerFactory.Create(b => b.AddProvider(logs)).CreateLogger<HouseholdService>());

    [Fact]
    public async Task A_new_household_view_has_utc_and_no_feed()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var view = await InitAsync(new HouseholdService(store, TimeProvider.System));

        Assert.Equal("UTC", view.TimeZoneId);
        Assert.False(view.CalendarFeedEnabled);
        Assert.Null(view.CalendarFeedSecretHash);
        Assert.Null(view.CalendarFeedRotatedAt);
    }

    [Fact]
    public async Task Time_zone_and_feed_events_round_trip_and_the_inline_view_matches_the_aggregate()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var service = new HouseholdService(store, TimeProvider.System);
        var created = await InitAsync(service);

        var zoned = await service.ChangeTimeZoneAsync(created.Version, "Europe/Copenhagen", Admin, Ct);
        var (rotated, secret) = await service.RotateCalendarFeedAsync(zoned.Version, Admin, Ct);

        var aggregate = await service.LoadAggregateAsync(Ct);
        var view = await service.GetViewAsync(Ct);
        Assert.Equal("Europe/Copenhagen", aggregate.TimeZoneId);
        Assert.Equal("Europe/Copenhagen", view!.TimeZoneId);
        Assert.Equal(created.Version + 2, rotated.Version);
        Assert.Equal(rotated.Version, aggregate.Version);
        Assert.Equal(CalendarFeedSecrets.Hash(secret), view.CalendarFeedSecretHash);
        Assert.Equal(aggregate.CalendarFeedSecretHash, view.CalendarFeedSecretHash);
        Assert.Equal(aggregate.CalendarFeedRotatedAt, view.CalendarFeedRotatedAt);
        Assert.True(view.CalendarFeedEnabled);

        var types = await db.ScalarAsMigratorAsync<string>("SELECT string_agg(type, ',' ORDER BY version) FROM subscribed.mt_events WHERE stream_id = 'household-default'");
        Assert.Equal("household.created.v1,household.member_granted.v1,household.time_zone_changed.v1,household.calendar_feed_rotated.v1", types);
    }

    [Fact]
    public async Task The_event_stream_and_the_view_hold_only_the_hash_never_the_secret()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var service = new HouseholdService(store, TimeProvider.System);
        var created = await InitAsync(service);

        var (_, secret) = await service.RotateCalendarFeedAsync(created.Version, Admin, Ct);

        var events = await db.ScalarAsMigratorAsync<string>("SELECT string_agg(data::text || headers::text, ' ') FROM subscribed.mt_events WHERE stream_id = 'household-default'");
        var view = await db.ScalarAsMigratorAsync<string>(ViewSql);
        Assert.DoesNotContain(secret, events, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, view, StringComparison.Ordinal);
        Assert.Contains(CalendarFeedSecrets.Hash(secret), events, StringComparison.Ordinal);
        Assert.Contains(CalendarFeedSecrets.Hash(secret), view, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_view_row_written_before_the_new_fields_loads_as_utc_with_the_feed_off()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var service = new HouseholdService(store, TimeProvider.System);
        var created = await InitAsync(service);
        var zoned = await service.ChangeTimeZoneAsync(created.Version, "Europe/Copenhagen", Admin, Ct);
        await service.RotateCalendarFeedAsync(zoned.Version, Admin, Ct);
        var json = await db.ScalarAsMigratorAsync<string>(ViewSql);
        Assert.Contains("timeZoneId", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("calendarFeedSecretHash", json, StringComparison.OrdinalIgnoreCase);

        await db.ExecuteAsMigratorAsync(
            """
            UPDATE subscribed.mt_doc_household_view
            SET data = (SELECT jsonb_object_agg(key, value) FROM jsonb_each(data)
                        WHERE lower(key) NOT IN ('timezoneid', 'calendarfeedsecrethash', 'calendarfeedrotatedat', 'calendarfeedenabled'))
            WHERE id = 'household-default'
            """);

        var legacy = await service.GetViewAsync(Ct);
        Assert.Equal("UTC", legacy!.TimeZoneId);
        Assert.False(legacy.CalendarFeedEnabled);
        Assert.Null(legacy.CalendarFeedSecretHash);
        Assert.Null(legacy.CalendarFeedRotatedAt);
        Assert.Equal("Synthetic Household", legacy.Name);
    }

    [Fact]
    public async Task Projection_rebuild_reproduces_the_view_with_time_zone_and_feed()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var service = new HouseholdService(store, TimeProvider.System);
        var created = await InitAsync(service);
        var zoned = await service.ChangeTimeZoneAsync(created.Version, "America/New_York", Admin, Ct);
        var (rotated, _) = await service.RotateCalendarFeedAsync(zoned.Version, Admin, Ct);
        await service.RevokeCalendarFeedAsync(rotated.Version, Admin, Ct);
        var (final, _) = await service.RotateCalendarFeedAsync(rotated.Version + 1, Admin, Ct);
        var before = await db.ScalarAsMigratorAsync<string>(ViewSql);

        await db.ExecuteAsMigratorAsync("DELETE FROM subscribed.mt_doc_household_view");
        await using (var migrator = MigratorStore(db))
        {
            Assert.Equal(1, await HouseholdProjectionRebuilder.RebuildAsync(migrator, Ct));
        }

        var rebuilt = await service.GetViewAsync(Ct);
        Assert.Equal("America/New_York", rebuilt!.TimeZoneId);
        Assert.Equal(final.CalendarFeedSecretHash, rebuilt.CalendarFeedSecretHash);
        Assert.Equal(final.Version, rebuilt.Version);
        Assert.Equal(before, await db.ScalarAsMigratorAsync<string>(ViewSql));
    }

    [Fact]
    public async Task Revoke_clears_the_feed_in_the_view_and_a_second_revoke_writes_nothing()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var service = new HouseholdService(store, TimeProvider.System);
        var created = await InitAsync(service);
        var (rotated, _) = await service.RotateCalendarFeedAsync(created.Version, Admin, Ct);

        var revoked = await service.RevokeCalendarFeedAsync(rotated.Version, Admin, Ct);
        var again = await service.RevokeCalendarFeedAsync(revoked.Version, Admin, Ct);

        Assert.False(revoked.CalendarFeedEnabled);
        Assert.Null(revoked.CalendarFeedRotatedAt);
        Assert.Equal(rotated.Version + 1, revoked.Version);
        Assert.Equal(revoked.Version, again.Version);
    }

    [Fact]
    public async Task Rotate_twice_replaces_the_hash_and_a_stale_version_conflicts()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var service = new HouseholdService(store, TimeProvider.System);
        var created = await InitAsync(service);
        var (first, firstSecret) = await service.RotateCalendarFeedAsync(created.Version, Admin, Ct);
        var (second, secondSecret) = await service.RotateCalendarFeedAsync(first.Version, Admin, Ct);

        Assert.NotEqual(firstSecret, secondSecret);
        Assert.Equal(CalendarFeedSecrets.Hash(secondSecret), second.CalendarFeedSecretHash);
        Assert.False(CalendarFeedSecrets.Matches(firstSecret, second.CalendarFeedSecretHash));
        var stale = await Assert.ThrowsAsync<EventStoreException>(() => service.RotateCalendarFeedAsync(first.Version, Admin, Ct));
        Assert.Equal(EventStoreFailureKind.ConcurrencyConflict, stale.Failure.Kind);
        Assert.True(CalendarFeedSecrets.Matches(secondSecret, (await service.GetViewAsync(Ct))!.CalendarFeedSecretHash));
    }

    [Fact]
    public async Task Change_time_zone_is_a_no_op_when_unchanged_and_rejects_an_unknown_zone()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var service = new HouseholdService(store, TimeProvider.System);
        var created = await InitAsync(service);

        var same = await service.ChangeTimeZoneAsync(created.Version, "UTC", Admin, Ct);
        var invalid = await Assert.ThrowsAsync<HouseholdDomainException>(() => service.ChangeTimeZoneAsync(created.Version, "Mars/Olympus_Mons", Admin, Ct));

        Assert.Equal(created.Version, same.Version);
        Assert.Equal(HouseholdErrorCodes.InvalidTimeZone, invalid.Code);
        Assert.Equal("timeZoneId", invalid.Field);
    }

    [Fact]
    public async Task Rotate_and_revoke_each_log_one_information_line_with_the_household_id_only()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var logs = new LogCapture();
        var service = ServiceWithLogs(store, logs);
        var created = await InitAsync(service);
        var zoned = await service.ChangeTimeZoneAsync(created.Version, "Europe/Copenhagen", Admin, Ct);

        var (rotated, secret) = await service.RotateCalendarFeedAsync(zoned.Version, Admin, Ct);
        var revoked = await service.RevokeCalendarFeedAsync(rotated.Version, Admin, Ct);
        await service.RevokeCalendarFeedAsync(revoked.Version, Admin, Ct); // no feed: no event, no log line

        var lines = logs.Entries;
        var rotatedLine = Assert.Single(lines, l => l.Contains("household.calendar_feed_rotated", StringComparison.Ordinal));
        var revokedLine = Assert.Single(lines, l => l.Contains("household.calendar_feed_revoked", StringComparison.Ordinal));
        Assert.StartsWith("Information ", rotatedLine, StringComparison.Ordinal);
        Assert.StartsWith("Information ", revokedLine, StringComparison.Ordinal);
        Assert.Contains("household-default", rotatedLine, StringComparison.Ordinal);
        Assert.Equal(2, lines.Count);
        Assert.Empty(logs.Find(secret, CalendarFeedSecrets.Hash(secret), "Europe/Copenhagen"));
    }
}
