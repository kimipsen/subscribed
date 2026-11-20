using Marten;
using Microsoft.Extensions.Logging;
using Subscribed.Household;
using Subscribed.Household.Domain;
using Subscribed.Platform.Database;
using Subscribed.Platform.EventStore;
using Subscribed.Platform.Identity;
using Subscribed.Subscriptions;
using Subscribed.Subscriptions.PaymentOptions;
using Subscribed.TestSupport;
using Xunit;

namespace Subscribed.Persistence.Tests;

/// <summary>MCP tokens in the household stream and view, the last-used document and the retry-once write path (#55, WP1).</summary>
public sealed class HouseholdMcpTokenPersistenceTests(PostgresFixture pg)
{
    private const string Issuer = "https://idp.example.invalid/realms/synthetic";
    private const string ViewSql = "SELECT data::text FROM subscribed.mt_doc_household_view WHERE id = 'household-default'";
    private static readonly IReadOnlyList<IEventStoreModule> Modules = [new HouseholdModule(), new SubscriptionsModule(), new PaymentOptionsModule()];
    private static readonly string MemberA = MemberIdentity.MemberId(Issuer, "subject-a");
    private static readonly string MemberB = MemberIdentity.MemberId(Issuer, "subject-b");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WriteContext Ctx(string memberId) => WriteContext.Mcp(memberId, "0123456789abcdef", "00-test");

    private static WriteContext Admin => new(WriteContext.AdminCli, "00-test");

    private static DocumentStore AppStore(TestDatabase db) => SubscribedStore.Create(db.AppConnectionString, PostgresFixture.Schema, StoreRole.App, Modules);

    private static DocumentStore MigratorStore(TestDatabase db) => SubscribedStore.Create(db.MigratorConnectionString, PostgresFixture.Schema, StoreRole.Migrator, Modules);

    private static async Task<HouseholdView> InitWithTwoMembersAsync(HouseholdService service)
    {
        await service.InitializeAsync("Synthetic Household", Issuer, "subject-a", "Member A", Admin, Ct);
        return await service.GrantAsync(Issuer, "subject-b", "Member B", Admin, Ct);
    }

    private static WriteContext Web(string memberId) => WriteContext.Member(memberId, "00-test");

    [Fact]
    public async Task Create_then_revoke_round_trips_through_events_view_and_aggregate()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var service = new HouseholdService(store, TimeProvider.System);
        var start = await InitWithTwoMembersAsync(service);

        var (view, token, secret) = await service.CreateMcpTokenAsync(MemberA, " Claude Code ", Web(MemberA), Ct);

        Assert.Equal(start.Version + 1, view.Version);
        Assert.Equal("Claude Code", token.Name);
        Assert.Null(token.LastUsedAt);
        Assert.Matches("^[0-9a-f]{16}$", token.Id);
        var row = Assert.Single(view.McpTokens);
        Assert.Equal(token.Id, row.TokenId);
        Assert.Equal(MemberA, row.MemberId);
        Assert.True(McpTokenSecrets.Matches(secret, row.SecretHash));
        var aggregate = await service.LoadAggregateAsync(Ct);
        Assert.Equal(token.Id, Assert.Single(aggregate.McpTokens).TokenId);
        Assert.Equal(view.Version, aggregate.Version);

        var revoked = await service.RevokeMcpTokenAsync(MemberA, token.Id, Web(MemberA), Ct);

        Assert.Empty(revoked.McpTokens);
        Assert.Equal(view.Version + 1, revoked.Version);
        Assert.Empty((await service.LoadAggregateAsync(Ct)).McpTokens);
        var types = await db.ScalarAsMigratorAsync<string>("SELECT string_agg(type, ',' ORDER BY version) FROM subscribed.mt_events WHERE stream_id = 'household-default'");
        Assert.EndsWith("household.mcp_token_created.v1,household.mcp_token_revoked.v1", types, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_stream_headers_and_view_hold_only_the_hash_never_the_secret()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var service = new HouseholdService(store, TimeProvider.System);
        await InitWithTwoMembersAsync(service);

        var (_, _, secret) = await service.CreateMcpTokenAsync(MemberA, "x", Web(MemberA), Ct);

        var events = await db.ScalarAsMigratorAsync<string>("SELECT string_agg(data::text || headers::text, ' ') FROM subscribed.mt_events WHERE stream_id = 'household-default'");
        var view = await db.ScalarAsMigratorAsync<string>(ViewSql);
        Assert.DoesNotContain(secret, events, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, view, StringComparison.Ordinal);
        Assert.Contains(McpTokenSecrets.Hash(secret), events, StringComparison.Ordinal);
        Assert.Contains(McpTokenSecrets.Hash(secret), view, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_projection_rebuild_reproduces_the_token_list()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var service = new HouseholdService(store, TimeProvider.System);
        await InitWithTwoMembersAsync(service);
        var (_, first, _) = await service.CreateMcpTokenAsync(MemberA, "first", Web(MemberA), Ct);
        await service.CreateMcpTokenAsync(MemberA, "second", Web(MemberA), Ct);
        await service.CreateMcpTokenAsync(MemberB, "other", Web(MemberB), Ct);
        await service.RevokeMcpTokenAsync(MemberA, first.Id, Web(MemberA), Ct);
        var before = await db.ScalarAsMigratorAsync<string>(ViewSql);
        var tokens = (await service.GetViewAsync(Ct))!.McpTokens.Select(t => t.Name).Order().ToList();

        await db.ExecuteAsMigratorAsync("DELETE FROM subscribed.mt_doc_household_view");
        await using (var migrator = MigratorStore(db))
        {
            Assert.Equal(1, await HouseholdProjectionRebuilder.RebuildAsync(migrator, Ct));
        }

        Assert.Equal(["other", "second"], tokens);
        Assert.Equal(tokens, (await service.GetViewAsync(Ct))!.McpTokens.Select(t => t.Name).Order().ToList());
        Assert.Equal(before, await db.ScalarAsMigratorAsync<string>(ViewSql));
    }

    [Fact]
    public async Task A_view_row_written_before_mcp_tokens_loads_with_an_empty_list()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var service = new HouseholdService(store, TimeProvider.System);
        await InitWithTwoMembersAsync(service);
        await service.CreateMcpTokenAsync(MemberA, "x", Web(MemberA), Ct);
        await db.ExecuteAsMigratorAsync(
            """
            UPDATE subscribed.mt_doc_household_view
            SET data = (SELECT jsonb_object_agg(key, value) FROM jsonb_each(data) WHERE lower(key) <> 'mcptokens')
            WHERE id = 'household-default'
            """);

        var legacy = await service.GetViewAsync(Ct);

        Assert.NotNull(legacy);
        Assert.Empty(legacy.McpTokens);
        Assert.Equal("Synthetic Household", legacy.Name);
    }

    [Fact]
    public async Task Revoking_a_member_drops_their_tokens_in_the_view_and_a_re_grant_has_none()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var service = new HouseholdService(store, TimeProvider.System);
        await InitWithTwoMembersAsync(service);
        await service.CreateMcpTokenAsync(MemberB, "b", Web(MemberB), Ct);
        var (_, keep, _) = await service.CreateMcpTokenAsync(MemberA, "a", Web(MemberA), Ct);

        var afterRevoke = await service.RevokeAsync(MemberB, Admin, Ct);
        var afterGrant = await service.GrantAsync(Issuer, "subject-b", "Member B again", Admin, Ct);

        Assert.Equal(keep.Id, Assert.Single(afterRevoke.McpTokens).TokenId);
        Assert.Equal(keep.Id, Assert.Single(afterGrant.McpTokens).TokenId);
        Assert.DoesNotContain((await service.LoadAggregateAsync(Ct)).McpTokens, t => t.MemberId == MemberB);
    }

    [Fact]
    public async Task Create_and_revoke_log_the_token_id_only()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var logs = new LogCapture();
        var service = new HouseholdService(store, TimeProvider.System, LoggerFactory.Create(b => b.AddProvider(logs)).CreateLogger<HouseholdService>());
        await InitWithTwoMembersAsync(service);

        var (_, token, secret) = await service.CreateMcpTokenAsync(MemberA, "Distinctive Token Name", Web(MemberA), Ct);
        await service.RevokeMcpTokenAsync(MemberA, token.Id, Web(MemberA), Ct);

        var lines = logs.Entries;
        Assert.Single(lines, l => l.Contains("household.mcp_token_created " + token.Id, StringComparison.Ordinal));
        Assert.Single(lines, l => l.Contains("household.mcp_token_revoked " + token.Id, StringComparison.Ordinal));
        Assert.Empty(logs.Find(secret, McpTokenSecrets.Hash(secret), "Distinctive Token Name"));
    }

    [Fact]
    public async Task A_token_use_row_is_upserted_as_the_app_role_and_listed()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var uses = new MartenMcpTokenUseStore(store);
        var first = new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero);

        await uses.UpsertAsync("0123456789abcdef", first, Ct);
        await uses.UpsertAsync("0123456789abcdef", first.AddHours(1), Ct);
        await uses.UpsertAsync("fedcba9876543210", first, Ct);

        var listed = await uses.ListAsync(["0123456789abcdef", "00000000deadbeef"], Ct);
        Assert.Equal(first.AddHours(1), listed["0123456789abcdef"]);
        Assert.False(listed.ContainsKey("00000000deadbeef"));
        Assert.Equal(2, await db.ScalarAsMigratorAsync<long>("SELECT count(*) FROM subscribed.mt_doc_mcp_token_use"));
        var stored = await db.ScalarAsMigratorAsync<string>("SELECT string_agg(data::text, ' ') FROM subscribed.mt_doc_mcp_token_use");
        Assert.DoesNotContain("hash", stored, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await uses.ListAsync([], Ct));
    }

    [Fact]
    public async Task A_missing_token_use_grant_fails_the_readiness_probe_and_the_upsert()
    {
        await using var db = await pg.CreateDatabaseAsync();
        var ready = await ReadinessProbe.CheckAsync(db.AppConnectionString, PostgresFixture.Schema, Ct);
        Assert.True(ready.Database && ready.Schema && ready.Privileges, string.Join(", ", ready.Problems));

        await db.ExecuteAsMigratorAsync($"REVOKE UPDATE ON subscribed.mt_doc_mcp_token_use FROM {pg.AppRole}");

        var missing = await ReadinessProbe.CheckAsync(db.AppConnectionString, PostgresFixture.Schema, Ct);
        Assert.False(missing.Privileges);
        Assert.Contains("privilege-missing:UPDATE:subscribed.mt_doc_mcp_token_use", missing.Problems);
    }

    // Forced interleaving of the retry-once path through the IHouseholdWriteHook seam.

    private sealed class ScriptedHook(Func<int, long, CancellationToken, Task> action) : IHouseholdWriteHook
    {
        private int _calls;

        public List<long> LoadedVersions { get; } = [];

        public int Calls => Volatile.Read(ref _calls);

        public Task OnLoadedAsync(long loadedVersion, CancellationToken ct)
        {
            lock (LoadedVersions)
            {
                LoadedVersions.Add(loadedVersion);
            }

            return action(Interlocked.Increment(ref _calls), loadedVersion, ct);
        }
    }

    private static async Task<long> TokenEventCountAsync(TestDatabase db) =>
        await db.ScalarAsMigratorAsync<long>("SELECT count(*) FROM subscribed.mt_events WHERE type LIKE 'household.mcp_token_%'");

    [Fact]
    public async Task Create_conflicts_once_then_succeeds_with_the_decision_re_run_on_fresh_state()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var plain = new HouseholdService(store, TimeProvider.System);
        var start = await InitWithTwoMembersAsync(plain);
        var hook = new ScriptedHook(async (attempt, _, ct) =>
        {
            if (attempt == 1)
            {
                await plain.ChangeMemberLabelAsync(null, MemberB, "Renamed concurrently", Web(MemberB), ct);
            }
        });
        var service = new HouseholdService(store, TimeProvider.System, null, hook);

        var (view, token, secret) = await service.CreateMcpTokenAsync(MemberA, "x", Web(MemberA), Ct);

        Assert.Equal(2, hook.Calls);
        Assert.Equal([start.Version, start.Version + 1], hook.LoadedVersions); // the retry loaded the competing event
        Assert.Equal(start.Version + 2, view.Version);
        Assert.Equal(1, await TokenEventCountAsync(db));
        Assert.True(McpTokenSecrets.Matches(secret, Assert.Single(view.McpTokens, t => t.TokenId == token.Id).SecretHash));
    }

    [Fact]
    public async Task Create_conflicting_on_both_attempts_surfaces_the_conflict_and_appends_nothing()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var plain = new HouseholdService(store, TimeProvider.System);
        await InitWithTwoMembersAsync(plain);
        var n = 0;
        var hook = new ScriptedHook((_, _, ct) => plain.ChangeMemberLabelAsync(null, MemberB, "Concurrent " + Interlocked.Increment(ref n), Web(MemberB), ct));
        var service = new HouseholdService(store, TimeProvider.System, null, hook);

        var ex = await Assert.ThrowsAsync<EventStoreException>(() => service.CreateMcpTokenAsync(MemberA, "x", Web(MemberA), Ct));

        Assert.Equal(EventStoreFailureKind.ConcurrencyConflict, ex.Failure.Kind);
        Assert.Equal(2, hook.Calls);
        Assert.Equal(0, await TokenEventCountAsync(db));
    }

    [Fact]
    public async Task Create_whose_competitor_fills_the_cap_ends_in_too_many_tokens_and_appends_nothing()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var plain = new HouseholdService(store, TimeProvider.System);
        await InitWithTwoMembersAsync(plain);
        for (var i = 0; i < PreFilledTokens; i++)
        {
            await plain.CreateMcpTokenAsync(MemberA, "t" + i, Web(MemberA), Ct);
        }

        var hook = new ScriptedHook(async (attempt, _, ct) =>
        {
            if (attempt == 1)
            {
                await plain.CreateMcpTokenAsync(MemberA, "competitor", Web(MemberA), ct);
            }
        });
        var service = new HouseholdService(store, TimeProvider.System, null, hook);

        var ex = await Assert.ThrowsAsync<HouseholdDomainException>(() => service.CreateMcpTokenAsync(MemberA, "loser", Web(MemberA), Ct));

        Assert.Equal(HouseholdErrorCodes.TooManyTokens, ex.Code);
        Assert.Equal(1, hook.Calls); // the retry re-ran the decision and stopped at the cap before reaching the append
        var view = await plain.GetViewAsync(Ct);
        Assert.Equal(20, view!.McpTokens.Count(t => t.MemberId == MemberA));
        Assert.DoesNotContain(view.McpTokens, t => t.Name == "loser");
    }

    private const int PreFilledTokens = 19;

    [Fact]
    public async Task Revoke_conflicts_once_then_succeeds_and_conflicting_twice_appends_nothing()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var plain = new HouseholdService(store, TimeProvider.System);
        await InitWithTwoMembersAsync(plain);
        var (_, once, _) = await plain.CreateMcpTokenAsync(MemberA, "once", Web(MemberA), Ct);
        var (_, twice, _) = await plain.CreateMcpTokenAsync(MemberA, "twice", Web(MemberA), Ct);
        var n = 0;

        var oneConflict = new HouseholdService(store, TimeProvider.System, null, new ScriptedHook(async (attempt, _, ct) =>
        {
            if (attempt == 1)
            {
                await plain.ChangeMemberLabelAsync(null, MemberB, "Concurrent", Web(MemberB), ct);
            }
        }));
        var revoked = await oneConflict.RevokeMcpTokenAsync(MemberA, once.Id, Web(MemberA), Ct);
        Assert.DoesNotContain(revoked.McpTokens, t => t.TokenId == once.Id);

        var tokenEventsBefore = await TokenEventCountAsync(db);
        var alwaysConflict = new HouseholdService(store, TimeProvider.System, null,
            new ScriptedHook((_, _, ct) => plain.ChangeMemberLabelAsync(null, MemberB, "Concurrent again " + Interlocked.Increment(ref n), Web(MemberB), ct)));
        var ex = await Assert.ThrowsAsync<EventStoreException>(() => alwaysConflict.RevokeMcpTokenAsync(MemberA, twice.Id, Web(MemberA), Ct));

        Assert.Equal(EventStoreFailureKind.ConcurrencyConflict, ex.Failure.Kind);
        Assert.Equal(tokenEventsBefore, await TokenEventCountAsync(db));
        Assert.Contains((await plain.GetViewAsync(Ct))!.McpTokens, t => t.TokenId == twice.Id);
    }

    [Fact]
    public async Task Revoke_whose_competitor_already_revoked_the_token_ends_in_token_not_found()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var plain = new HouseholdService(store, TimeProvider.System);
        await InitWithTwoMembersAsync(plain);
        var (_, token, _) = await plain.CreateMcpTokenAsync(MemberA, "x", Web(MemberA), Ct);
        var service = new HouseholdService(store, TimeProvider.System, null, new ScriptedHook(async (attempt, _, ct) =>
        {
            if (attempt == 1)
            {
                await plain.RevokeMcpTokenAsync(MemberA, token.Id, Web(MemberA), ct);
            }
        }));

        var ex = await Assert.ThrowsAsync<HouseholdDomainException>(() => service.RevokeMcpTokenAsync(MemberA, token.Id, Web(MemberA), Ct));

        Assert.Equal(HouseholdErrorCodes.TokenNotFound, ex.Code);
        Assert.Equal(1, await db.ScalarAsMigratorAsync<long>("SELECT count(*) FROM subscribed.mt_events WHERE type = 'household.mcp_token_revoked.v1'"));
    }

    [Fact]
    public async Task A_member_cannot_revoke_another_members_token_and_the_write_context_leaves_headers_unchanged()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var store = AppStore(db);
        var service = new HouseholdService(store, TimeProvider.System);
        await InitWithTwoMembersAsync(service);

        var (_, token, _) = await service.CreateMcpTokenAsync(MemberA, "mine", WriteContext.Mcp(MemberA, "0123456789abcdef", "00-trace"), Ct);
        var ex = await Assert.ThrowsAsync<HouseholdDomainException>(() => service.RevokeMcpTokenAsync(MemberB, token.Id, Web(MemberB), Ct));

        Assert.Equal(HouseholdErrorCodes.TokenNotFound, ex.Code);
        var headers = await db.ScalarAsMigratorAsync<string>("SELECT headers::text FROM subscribed.mt_events WHERE type = 'household.mcp_token_created.v1'");
        Assert.Contains("\"actor\"", headers, StringComparison.Ordinal);
        Assert.DoesNotContain("source", headers, StringComparison.Ordinal); // WP3 owns header stamping
        Assert.DoesNotContain("tokenId", headers, StringComparison.Ordinal);
    }
}
