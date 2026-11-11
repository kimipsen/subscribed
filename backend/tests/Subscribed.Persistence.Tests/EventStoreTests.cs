using System.Xml.Linq;
using Marten;
using Npgsql;
using Subscribed.Household;
using Subscribed.Household.Domain;
using Subscribed.Platform.Database;
using Subscribed.Platform.DataProtection;
using Subscribed.Platform.EventStore;
using Subscribed.TestSupport;
using Xunit;

[assembly: AssemblyFixture(typeof(PostgresFixture))]

namespace Subscribed.Persistence.Tests;

public sealed class EventStoreTests(PostgresFixture pg)
{
    private const string Issuer = "https://idp.example.invalid/realms/synthetic";
    private static readonly IReadOnlyList<IEventStoreModule> Modules = [new HouseholdModule()];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DocumentStore AppStore(TestDatabase db) => SubscribedStore.Create(db.AppConnectionString, PostgresFixture.Schema, StoreRole.App, Modules);

    private static DocumentStore MigratorStore(TestDatabase db) => SubscribedStore.Create(db.MigratorConnectionString, PostgresFixture.Schema, StoreRole.Migrator, Modules);

    private static WriteContext Admin => new(WriteContext.AdminCli, "00-test");

    private static async Task<HouseholdView> InitAsync(HouseholdService service) =>
        await service.InitializeAsync("Synthetic Household", Issuer, "subject-a", "Member A", Admin, Ct);

    [Fact]
    public async Task AC1_append_new_stream_and_rehydrate_gives_identical_state_and_version_equals_event_count()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var service = new HouseholdService(store, TimeProvider.System);

        var created = await InitAsync(service);
        var renamed = await service.RenameAsync(created.Version, "Renamed Household", Admin, Ct);
        var granted = await service.GrantAsync(Issuer, "subject-b", "Member B", Admin, Ct);

        var aggregate = await service.LoadAggregateAsync(Ct);
        var count = await db.ScalarAsMigratorAsync<long>("SELECT count(*) FROM subscribed.mt_events WHERE stream_id = 'household-default'");

        Assert.Equal(2, created.Version); // created + owner granted
        Assert.Equal(3, renamed.Version);
        Assert.Equal(4, granted.Version);
        Assert.Equal(count, aggregate.Version);
        Assert.Equal(granted.Version, aggregate.Version);
        Assert.Equal("Renamed Household", aggregate.Name);
        Assert.Equal(granted.Members.Select(m => m.MemberId).Order(), aggregate.Members.Select(m => m.MemberId).Order());

        // Stored aliases are the explicit v1 names and headers carry actor/correlation only.
        var types = await db.ScalarAsMigratorAsync<string>("SELECT string_agg(type, ',' ORDER BY version) FROM subscribed.mt_events WHERE stream_id = 'household-default'");
        Assert.Equal("household.created.v1,household.member_granted.v1,household.renamed.v1,household.member_granted.v1", types);
        var headers = await db.ScalarAsMigratorAsync<string>("SELECT headers::text FROM subscribed.mt_events WHERE stream_id = 'household-default' AND version = 3");
        Assert.Contains("admin-cli", headers, StringComparison.Ordinal);
        Assert.Contains("00-test", headers, StringComparison.Ordinal);
        var data = await db.ScalarAsMigratorAsync<string>("SELECT data::text FROM subscribed.mt_events WHERE stream_id = 'household-default' AND version = 2");
        Assert.Contains("\"memberId\"", data, StringComparison.Ordinal);
        Assert.DoesNotContain("email", data, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AC2a_two_sessions_at_same_version_one_commits_other_conflicts_and_stream_gains_one_event()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var service = new HouseholdService(store, TimeProvider.System);
        await InitAsync(service);

        await using var s1 = store.LightweightSession();
        await using var s2 = store.LightweightSession();
        s1.Events.Append(HouseholdService.StreamId, 3, new HouseholdRenamed("One", DateTimeOffset.UtcNow));
        s2.Events.Append(HouseholdService.StreamId, 3, new HouseholdRenamed("Two", DateTimeOffset.UtcNow));
        var results = await Task.WhenAll(Capture(s1.SaveChangesAsync(Ct)), Capture(s2.SaveChangesAsync(Ct)));

        var failures = results.Where(e => e is not null).ToList();
        Assert.Single(failures);
        Assert.Equal(EventStoreFailureKind.ConcurrencyConflict, EventStoreErrors.Map(failures[0]!)?.Kind);
        Assert.IsAssignableFrom<JasperFx.ConcurrencyException>(failures[0]);
        Assert.Equal(3, await db.ScalarAsMigratorAsync<long>("SELECT count(*) FROM subscribed.mt_events WHERE stream_id = 'household-default'"));
    }

    [Fact]
    public async Task AC2b_stale_explicit_version_is_a_conflict_through_the_service()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var service = new HouseholdService(store, TimeProvider.System);
        var created = await InitAsync(service);
        await service.RenameAsync(created.Version, "First", Admin, Ct);

        var ex = await Assert.ThrowsAsync<EventStoreException>(() => service.RenameAsync(created.Version, "Stale", Admin, Ct));
        Assert.Equal(EventStoreFailureKind.ConcurrencyConflict, ex.Failure.Kind);

        // The raw library path (append at a stale version) is classified the same way.
        await using var session = store.LightweightSession();
        session.Events.Append(HouseholdService.StreamId, created.Version + 1, new HouseholdRenamed("Raw stale", DateTimeOffset.UtcNow));
        var raw = await Assert.ThrowsAsync<JasperFx.Events.EventStreamUnexpectedMaxEventIdException>(() => session.SaveChangesAsync(Ct));
        Assert.Equal(EventStoreFailureKind.ConcurrencyConflict, EventStoreErrors.Map(raw)?.Kind);
        Assert.Equal("First", (await service.GetViewAsync(Ct))!.Name);
    }

    [Fact]
    public async Task AC2c_concurrent_stream_creation_is_a_conflict()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var a = new HouseholdService(store, TimeProvider.System);
        var b = new HouseholdService(store, TimeProvider.System);

        var results = await Task.WhenAll(
            Capture(a.InitializeAsync("A", Issuer, "subject-a", "A", Admin, Ct)),
            Capture(b.InitializeAsync("B", Issuer, "subject-b", "B", Admin, Ct)));

        var failures = results.Where(e => e is not null).ToList();
        Assert.Single(failures);
        var failure = Assert.IsType<EventStoreException>(failures[0]);
        Assert.Equal(EventStoreFailureKind.ConcurrencyConflict, failure.Failure.Kind);

        // Direct StartStream on an existing stream: ExistingStreamIdCollisionException → conflict.
        await using var session = store.LightweightSession();
        session.Events.StartStream<HouseholdView>(HouseholdService.StreamId, new HouseholdCreated("Dup", DateTimeOffset.UtcNow));
        var raw = await Assert.ThrowsAsync<Marten.Exceptions.ExistingStreamIdCollisionException>(() => session.SaveChangesAsync(Ct));
        Assert.Equal(EventStoreFailureKind.ConcurrencyConflict, EventStoreErrors.Map(raw)?.Kind);
        Assert.Equal(2, await db.ScalarAsMigratorAsync<long>("SELECT count(*) FROM subscribed.mt_events WHERE stream_id = 'household-default'"));
    }

    [Fact]
    public async Task AC3_failure_in_view_upsert_rolls_back_events_stream_version_and_view()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var service = new HouseholdService(store, TimeProvider.System);
        var created = await InitAsync(service);
        await db.ExecuteAsMigratorAsync(
            """
            CREATE FUNCTION subscribed.test_fail_inject() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.data->>'name' = 'FAIL-INJECT' THEN RAISE EXCEPTION 'injected'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER test_fail_inject BEFORE INSERT OR UPDATE ON subscribed.mt_doc_household_view
              FOR EACH ROW EXECUTE FUNCTION subscribed.test_fail_inject();
            GRANT EXECUTE ON FUNCTION subscribed.test_fail_inject() TO PUBLIC;
            """);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => service.RenameAsync(created.Version, "FAIL-INJECT", Admin, Ct));
        }
        finally
        {
            await db.ExecuteAsMigratorAsync("DROP TRIGGER test_fail_inject ON subscribed.mt_doc_household_view; DROP FUNCTION subscribed.test_fail_inject();");
        }

        Assert.Equal(2, await db.ScalarAsMigratorAsync<long>("SELECT count(*) FROM subscribed.mt_events WHERE stream_id = 'household-default'"));
        Assert.Equal(2, await db.ScalarAsMigratorAsync<long>("SELECT version FROM subscribed.mt_streams WHERE id = 'household-default'"));
        var view = await service.GetViewAsync(Ct);
        Assert.Equal("Synthetic Household", view!.Name);
        Assert.Equal(2, view.Version);
    }

    [Fact]
    public async Task AC4_rebuild_as_migrator_restores_identical_view_document()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var service = new HouseholdService(store, TimeProvider.System);
        var created = await InitAsync(service);
        await service.RenameAsync(created.Version, "Before Rebuild", Admin, Ct);
        await service.GrantAsync(Issuer, "subject-b", "Member B", Admin, Ct);
        var before = await db.ScalarAsMigratorAsync<string>("SELECT data::text FROM subscribed.mt_doc_household_view WHERE id = 'household-default'");

        await db.ExecuteAsMigratorAsync("DELETE FROM subscribed.mt_doc_household_view");
        Assert.Null(await service.GetViewAsync(Ct));

        await using (var migrator = MigratorStore(db))
        {
            Assert.Equal(1, await HouseholdProjectionRebuilder.RebuildAsync(migrator, Ct));
        }

        var after = await db.ScalarAsMigratorAsync<string>("SELECT data::text FROM subscribed.mt_doc_household_view WHERE id = 'household-default'");
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task AC5_new_store_on_same_database_rehydrates_identical_values()
    {
        await using var db = await pg.CreateDatabaseAsync();
        HouseholdView written;
        await using (var a = AppStore(db))
        {
            var service = new HouseholdService(a, TimeProvider.System);
            var created = await InitAsync(service);
            written = await service.RenameAsync(created.Version, "Survives Restart", Admin, Ct);
        }

        await using var b = AppStore(db);
        var reread = new HouseholdService(b, TimeProvider.System);
        var view = await reread.GetViewAsync(Ct);
        var aggregate = await reread.LoadAggregateAsync(Ct);
        Assert.Equal(written.Name, view!.Name);
        Assert.Equal(written.Version, view.Version);
        Assert.Equal(written.Name, aggregate.Name);
        Assert.Equal(written.Version, aggregate.Version);
    }

    [Theory]
    [InlineData("DELETE FROM subscribed.mt_events")]
    [InlineData("UPDATE subscribed.mt_events SET type = type")]
    [InlineData("TRUNCATE subscribed.mt_events")]
    [InlineData("CREATE TABLE subscribed.intruder (id int)")]
    [InlineData("DELETE FROM subscribed.mt_doc_household_view")]
    [InlineData("DELETE FROM subscribed.mt_doc_dp_key")]
    [InlineData("UPDATE subscribed.mt_doc_dp_key SET data = data")]
    public async Task AC20_app_role_cannot_modify_history_or_create_objects(string sql)
    {
        await using var db = await pg.CreateDatabaseAsync();
        var ex = await Assert.ThrowsAsync<PostgresException>(() => db.ExecuteAsAppAsync(sql));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        Assert.Equal(EventStoreFailureKind.PrivilegeMissing, EventStoreErrors.Map(ex)?.Kind);
    }

    [Fact]
    public async Task AC19_readiness_checks_pass_as_app_role_with_exact_grants_and_fail_without_one()
    {
        await using var db = await pg.CreateDatabaseAsync();
        var ready = await ReadinessProbe.CheckAsync(db.AppConnectionString, PostgresFixture.Schema, Ct);
        Assert.True(ready.Database && ready.Schema && ready.Privileges, string.Join(", ", ready.Problems));

        await db.ExecuteAsMigratorAsync($"REVOKE INSERT ON subscribed.mt_events FROM {pg.AppRole}");
        var missing = await ReadinessProbe.CheckAsync(db.AppConnectionString, PostgresFixture.Schema, Ct);
        Assert.False(missing.Privileges);
        Assert.Contains("privilege-missing:INSERT:subscribed.mt_events", missing.Problems);

        await db.ExecuteAsMigratorAsync($"GRANT INSERT, UPDATE ON subscribed.mt_events TO {pg.AppRole}");
        var forbidden = await ReadinessProbe.CheckAsync(db.AppConnectionString, PostgresFixture.Schema, Ct);
        Assert.False(forbidden.Privileges);
        Assert.Contains("privilege-forbidden:UPDATE:mt_events", forbidden.Problems);
    }

    [Fact]
    public async Task Readiness_fails_for_unmigrated_schema_version()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await db.ExecuteAsMigratorAsync("DELETE FROM subscribed.schema_migrations");
        var result = await ReadinessProbe.CheckAsync(db.AppConnectionString, PostgresFixture.Schema, Ct);
        Assert.False(result.Schema);
        Assert.Contains("schema-version-mismatch", result.Problems);
    }

    [Fact]
    public async Task Migration_is_idempotent_and_leaves_no_marten_schema_drift()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await SchemaMigrator.MigrateAsync(db.MigratorConnectionString, PostgresFixture.Schema, pg.AppRole, Modules, Ct);
        await using var migrator = MigratorStore(db);
        await migrator.Storage.Database.AssertDatabaseMatchesConfigurationAsync(Ct);
        var ready = await ReadinessProbe.CheckAsync(db.AppConnectionString, PostgresFixture.Schema, Ct);
        Assert.True(ready.Privileges, string.Join(", ", ready.Problems));
    }

    [Fact]
    public async Task Functions_created_by_marten_are_security_invoker_and_owned_by_migrator()
    {
        await using var db = await pg.CreateDatabaseAsync();
        var bad = await db.ScalarAsMigratorAsync<long>(
            "SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname = 'subscribed' AND (p.prosecdef OR pg_get_userbyid(p.proowner) <> current_user)");
        Assert.Equal(0, bad);
    }

    [Fact]
    public async Task Unknown_event_alias_in_stream_fails_loudly_on_rehydration()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var service = new HouseholdService(store, TimeProvider.System);
        await InitAsync(service);
        await db.ExecuteAsMigratorAsync(
            """
            UPDATE subscribed.mt_streams SET version = 3 WHERE id = 'household-default';
            INSERT INTO subscribed.mt_events (seq_id, id, stream_id, version, data, type, timestamp, tenant_id, mt_dotnet_type, is_archived)
            VALUES (nextval('subscribed.mt_events_sequence'), gen_random_uuid(), 'household-default', 3, '{}'::jsonb, 'household.bogus.v1', now(), '*DEFAULT*', NULL, false);
            """);

        await Assert.ThrowsAnyAsync<Exception>(() => service.LoadAggregateAsync(Ct));
    }

    [Fact]
    public async Task Data_protection_keys_are_inserted_and_read_back_as_app_role()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var repository = new MartenXmlRepository(store);
        repository.StoreElement(new XElement("key", new XAttribute("id", "k-1")), "key-k-1");
        repository.StoreElement(new XElement("key", new XAttribute("id", "k-2")), "key-k-2");

        var elements = repository.GetAllElements();
        Assert.Equal(["k-1", "k-2"], elements.Select(e => e.Attribute("id")!.Value).Order());
    }

    [Fact]
    public async Task Revoke_applies_to_the_view_and_last_member_cannot_be_revoked()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var service = new HouseholdService(store, TimeProvider.System);
        var created = await InitAsync(service);
        var ownerId = created.Members.Single().MemberId;

        await Assert.ThrowsAsync<HouseholdDomainException>(() => service.RevokeAsync(ownerId, Admin, Ct));
        var granted = await service.GrantAsync(Issuer, "subject-b", "Member B", Admin, Ct);
        var again = await service.GrantAsync(Issuer, "subject-b", "Member B", Admin, Ct);
        Assert.Equal(granted.Version, again.Version); // re-grant is a no-op

        var revoked = await service.RevokeAsync(ownerId, Admin, Ct);
        Assert.Null(revoked.FindMember(Issuer, "subject-a"));
        Assert.NotNull(revoked.FindMember(Issuer, "subject-b"));
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
