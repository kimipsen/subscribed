using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Subscribed.Household;
using Subscribed.TestSupport;
using Xunit;

namespace Subscribed.Api.Tests;

/// <summary>MCP token management endpoints (#55, WP1): contracts, secrecy, ownership, If-Match freshness, retries and the kill switch.</summary>
public sealed class McpTokenApiTests(PostgresFixture pg)
{
    private const string Tokens = "/api/household/mcp-tokens";
    private const string Household = "/api/household";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<ApiTestHost> StartSignedInAsync(PostgresFixture pg, Action<Dictionary<string, string?>>? configure = null, Action<IServiceCollection>? services = null)
    {
        var host = await ApiTestHost.StartAsync(pg, configure, services: services);
        await host.InitHouseholdAsync("subject-a");
        (await host.LoginAsync("subject-a")).Dispose();
        return host;
    }

    private static async Task<JsonElement> CreateAsync(ApiTestHost host, string name = "Claude Code")
    {
        using var response = await host.SendAsync(HttpMethod.Post, Tokens, new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ApiTestHost.JsonAsync(response);
    }

    private static async Task<JsonElement> ListAsync(ApiTestHost host)
    {
        using var response = await host.SendAsync(HttpMethod.Get, Tokens);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiTestHost.JsonAsync(response);
    }

    private static HouseholdService Service(ApiTestHost host) => host.App.Services.GetRequiredService<HouseholdService>();

    [Fact]
    public async Task Create_returns_201_with_the_secret_once_and_the_list_never_shows_it()
    {
        await using var host = await StartSignedInAsync(pg);

        using var response = await host.SendAsync(HttpMethod.Post, Tokens, new { name = "  Claude Code  " });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await ApiTestHost.JsonAsync(response);
        Assert.Equal(["household", "secret", "token"], body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        var secret = body.GetProperty("secret").GetString()!;
        Assert.Matches("^[A-Za-z0-9_-]{43}$", secret);
        var token = body.GetProperty("token");
        Assert.Equal(["createdAt", "id", "lastUsedAt", "name"], token.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("Claude Code", token.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, token.GetProperty("lastUsedAt").ValueKind);
        Assert.Equal(3, body.GetProperty("household").GetProperty("version").GetInt64());
        Assert.Equal(["calendarFeed", "id", "members", "name", "timeZoneId", "version"], body.GetProperty("household").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));

        var list = await ListAsync(host);
        var row = Assert.Single(list.GetProperty("tokens").EnumerateArray());
        Assert.Equal(token.GetProperty("id").GetString(), row.GetProperty("id").GetString());
        Assert.DoesNotContain(secret, list.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(McpTokenSecrets.Hash(secret), list.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("hash", list.GetRawText(), StringComparison.OrdinalIgnoreCase);
        using var household = await host.SendAsync(HttpMethod.Get, Household);
        Assert.DoesNotContain(secret, await household.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
        Assert.DoesNotContain(McpTokenSecrets.Hash(secret), await household.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\\nname")]
    public async Task Create_rejects_an_invalid_name_with_a_name_field_error(string name)
    {
        await using var host = await StartSignedInAsync(pg);

        using var response = await host.SendAsync(HttpMethod.Post, Tokens, rawBody: "{\"name\":\"" + name + "\"}");

        await ApiTestHost.AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation-failed");
        Assert.True((await ApiTestHost.JsonAsync(response)).GetProperty("errors").TryGetProperty("name", out _));
        Assert.Empty((await ListAsync(host)).GetProperty("tokens").EnumerateArray());
    }

    [Fact]
    public async Task Create_without_a_name_or_with_a_65_character_name_is_a_validation_failure()
    {
        await using var host = await StartSignedInAsync(pg);

        using var missing = await host.SendAsync(HttpMethod.Post, Tokens, new { });
        using var tooLong = await host.SendAsync(HttpMethod.Post, Tokens, new { name = new string('n', 65) });
        using var exact = await host.SendAsync(HttpMethod.Post, Tokens, new { name = new string('n', 64) });

        await ApiTestHost.AssertProblemAsync(missing, HttpStatusCode.BadRequest, "validation-failed");
        await ApiTestHost.AssertProblemAsync(tooLong, HttpStatusCode.BadRequest, "validation-failed");
        Assert.Equal(HttpStatusCode.Created, exact.StatusCode);
    }

    [Fact]
    public async Task The_twenty_first_token_is_a_409_too_many_tokens_and_a_revoke_frees_a_slot()
    {
        await using var host = await StartSignedInAsync(pg);
        string first = "";
        for (var i = 0; i < 20; i++)
        {
            var created = await CreateAsync(host, "t" + i);
            first = i == 0 ? created.GetProperty("token").GetProperty("id").GetString()! : first;
        }

        using var over = await host.SendAsync(HttpMethod.Post, Tokens, new { name = "one more" });
        await ApiTestHost.AssertProblemAsync(over, HttpStatusCode.Conflict, "too-many-tokens");
        Assert.Equal(20, (await ListAsync(host)).GetProperty("tokens").GetArrayLength());

        using (var revoke = await host.SendAsync(HttpMethod.Delete, $"{Tokens}/{first}"))
        {
            Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        }

        await CreateAsync(host, "fits again");
    }

    [Fact]
    public async Task Two_tokens_revoke_independently_and_a_second_revoke_is_404()
    {
        await using var host = await StartSignedInAsync(pg);
        var one = (await CreateAsync(host, "one")).GetProperty("token").GetProperty("id").GetString()!;
        var two = (await CreateAsync(host, "two")).GetProperty("token").GetProperty("id").GetString()!;

        using var revoked = await host.SendAsync(HttpMethod.Delete, $"{Tokens}/{one}");

        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.Equal("no-store", revoked.Headers.CacheControl?.ToString());
        var body = await ApiTestHost.JsonAsync(revoked);
        Assert.Equal(["household"], body.EnumerateObject().Select(p => p.Name));
        Assert.Equal(two, Assert.Single((await ListAsync(host)).GetProperty("tokens").EnumerateArray()).GetProperty("id").GetString());
        using var again = await host.SendAsync(HttpMethod.Delete, $"{Tokens}/{one}");
        await ApiTestHost.AssertProblemAsync(again, HttpStatusCode.NotFound, "not-found");
        using var unknown = await host.SendAsync(HttpMethod.Delete, $"{Tokens}/0000000000000000");
        await ApiTestHost.AssertProblemAsync(unknown, HttpStatusCode.NotFound, "not-found");
        using var junk = await host.SendAsync(HttpMethod.Delete, $"{Tokens}/not-a-token-id");
        await ApiTestHost.AssertProblemAsync(junk, HttpStatusCode.NotFound, "not-found");
    }

    [Fact]
    public async Task Members_see_and_revoke_only_their_own_tokens()
    {
        await using var host = await StartSignedInAsync(pg);
        var other = await Service(host).GrantAsync(ApiTestHost.Issuer, "subject-b", "Member B", new WriteContext(WriteContext.AdminCli, null), Ct);
        var otherMember = other.Members.Single(m => m.Subject == "subject-b").MemberId;
        var (_, foreign, _) = await Service(host).CreateMcpTokenAsync(otherMember, "foreign", WriteContext.Member(otherMember, null), Ct);
        var mine = (await CreateAsync(host, "mine")).GetProperty("token").GetProperty("id").GetString()!;

        var list = await ListAsync(host);
        using var revoke = await host.SendAsync(HttpMethod.Delete, $"{Tokens}/{foreign.Id}");

        Assert.Equal(mine, Assert.Single(list.GetProperty("tokens").EnumerateArray()).GetProperty("id").GetString());
        await ApiTestHost.AssertProblemAsync(revoke, HttpStatusCode.NotFound, "not-found");
        Assert.Contains((await Service(host).GetViewAsync(Ct))!.McpTokens, t => t.TokenId == foreign.Id);
    }

    [Fact]
    public async Task A_rename_right_after_create_or_revoke_succeeds_with_the_returned_version()
    {
        await using var host = await StartSignedInAsync(pg);

        var created = await CreateAsync(host);
        var version = created.GetProperty("household").GetProperty("version").GetInt64();
        using (var rename = await host.SendAsync(HttpMethod.Put, Household + "/name", new { name = "After create" }, ifMatch: $"\"{version}\""))
        {
            Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
            version = (await ApiTestHost.JsonAsync(rename)).GetProperty("version").GetInt64();
        }

        var tokenId = created.GetProperty("token").GetProperty("id").GetString()!;
        using var revoke = await host.SendAsync(HttpMethod.Delete, $"{Tokens}/{tokenId}");
        var afterRevoke = (await ApiTestHost.JsonAsync(revoke)).GetProperty("household").GetProperty("version").GetInt64();
        Assert.Equal(version + 1, afterRevoke);
        using var again = await host.SendAsync(HttpMethod.Put, Household + "/name", new { name = "After revoke" }, ifMatch: $"\"{afterRevoke}\"");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task Requests_without_a_session_with_only_an_mcp_token_or_without_csrf_are_rejected()
    {
        await using var host = await StartSignedInAsync(pg);
        var secret = (await CreateAsync(host)).GetProperty("secret").GetString()!;

        using (var noCsrf = await host.SendAsync(HttpMethod.Post, Tokens, new { name = "x" }, csrf: false))
        {
            await ApiTestHost.AssertProblemAsync(noCsrf, HttpStatusCode.Forbidden, "csrf-header-required");
        }

        using (var badOrigin = await host.SendAsync(HttpMethod.Post, Tokens, new { name = "x" }, origin: "https://evil.example"))
        {
            await ApiTestHost.AssertProblemAsync(badOrigin, HttpStatusCode.Forbidden, "origin-mismatch");
        }

        using var anonymous = new HttpClient(host.App.GetTestServer().CreateHandler()) { BaseAddress = new Uri(ApiTestHost.PublicOrigin) };
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Post, HttpMethod.Delete })
        {
            using var request = new HttpRequestMessage(method, new Uri(method == HttpMethod.Delete ? Tokens + "/0000000000000000" : Tokens, UriKind.Relative));
            request.Headers.Add("X-CSRF", "1");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            using var response = await anonymous.SendAsync(request, Ct);
            await ApiTestHost.AssertProblemAsync(response, HttpStatusCode.Unauthorized, "unauthenticated");
        }
    }

    [Fact]
    public async Task Create_and_revoke_conflict_cases_through_the_write_hook()
    {
        // (a) conflict on the first attempt only: the create succeeds on the retry; (b) conflict on both attempts: 412 with the
        // current version and no token event; (c) the competitor fills the cap: too-many-tokens.
        var script = new HookScript();
        await using var host = await StartSignedInAsync(pg, services: s => s.AddSingleton<IHouseholdWriteHook>(script));
        var plain = HouseholdWith(host, null);
        script.Plain = plain;
        var memberId = (await plain.GetViewAsync(Ct))!.Members.Single().MemberId;

        script.Mode = HookMode.ConflictOnce;
        var once = await CreateAsync(host, "after one conflict");
        Assert.Equal(2, script.Calls);
        Assert.Single((await Service(host).GetViewAsync(Ct))!.McpTokens);

        script.Reset(HookMode.ConflictAlways);
        using (var twice = await host.SendAsync(HttpMethod.Post, Tokens, new { name = "never" }))
        {
            await ApiTestHost.AssertProblemAsync(twice, HttpStatusCode.PreconditionFailed, "concurrency-conflict");
            var current = (await ApiTestHost.JsonAsync(twice)).GetProperty("currentVersion").GetInt64();
            Assert.Equal((await Service(host).GetViewAsync(Ct))!.Version, current);
        }

        Assert.Equal(2, script.Calls);
        Assert.DoesNotContain((await Service(host).GetViewAsync(Ct))!.McpTokens, t => t.Name == "never");

        script.Reset(HookMode.None);
        for (var i = 0; i < 18; i++)
        {
            await plain.CreateMcpTokenAsync(memberId, "fill" + i, WriteContext.Member(memberId, null), Ct);
        }

        script.Reset(HookMode.FillCapOnce);
        script.MemberId = memberId;
        using (var capped = await host.SendAsync(HttpMethod.Post, Tokens, new { name = "loser" }))
        {
            await ApiTestHost.AssertProblemAsync(capped, HttpStatusCode.Conflict, "too-many-tokens");
        }

        Assert.Equal(20, (await Service(host).GetViewAsync(Ct))!.McpTokens.Count);

        script.Reset(HookMode.ConflictOnce);
        var id = once.GetProperty("token").GetProperty("id").GetString()!;
        using (var revoke = await host.SendAsync(HttpMethod.Delete, $"{Tokens}/{id}"))
        {
            Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        }

        script.Reset(HookMode.ConflictAlways);
        var remaining = (await Service(host).GetViewAsync(Ct))!.McpTokens[0].TokenId;
        using (var revoke = await host.SendAsync(HttpMethod.Delete, $"{Tokens}/{remaining}"))
        {
            await ApiTestHost.AssertProblemAsync(revoke, HttpStatusCode.PreconditionFailed, "concurrency-conflict");
        }

        Assert.Contains((await Service(host).GetViewAsync(Ct))!.McpTokens, t => t.TokenId == remaining);
    }

    private static HouseholdService HouseholdWith(ApiTestHost host, IHouseholdWriteHook? hook) =>
        new(host.App.Services.GetRequiredService<Marten.IDocumentStore>(), TimeProvider.System, null, hook);

    private enum HookMode
    {
        None,
        ConflictOnce,
        ConflictAlways,
        FillCapOnce,
    }

    private sealed class HookScript : IHouseholdWriteHook
    {
        private int _calls;
        private int _noise;

        public HookMode Mode { get; set; }

        public HouseholdService? Plain { get; set; }

        public string? MemberId { get; set; }

        public int Calls => Volatile.Read(ref _calls);

        public void Reset(HookMode mode)
        {
            Interlocked.Exchange(ref _calls, 0);
            Mode = mode;
        }

        public async Task OnLoadedAsync(long loadedVersion, CancellationToken ct)
        {
            var attempt = Interlocked.Increment(ref _calls);
            var plain = Plain ?? throw new InvalidOperationException("The hook has no competing service.");
            switch (Mode)
            {
                case HookMode.ConflictOnce when attempt == 1:
                case HookMode.ConflictAlways:
                    var view = await plain.GetViewAsync(ct);
                    await plain.ChangeMemberLabelAsync(null, view!.Members[0].MemberId, "Noise " + Interlocked.Increment(ref _noise), WriteContext.Member(view.Members[0].MemberId, null), ct);
                    break;
                case HookMode.FillCapOnce when attempt == 1:
                    await plain.CreateMcpTokenAsync(MemberId!, "competitor", WriteContext.Member(MemberId!, null), ct);
                    break;
                default:
                    break;
            }
        }
    }

    [Fact]
    public async Task A_plain_household_edit_racing_a_token_create_ends_in_success_or_the_defined_conflict()
    {
        var script = new HookScript();
        await using var host = await StartSignedInAsync(pg, services: s => s.AddSingleton<IHouseholdWriteHook>(script));
        script.Plain = HouseholdWith(host, null);
        script.Mode = HookMode.ConflictOnce;

        using var response = await host.SendAsync(HttpMethod.Post, Tokens, new { name = "racing" });

        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.PreconditionFailed });
        var view = (await Service(host).GetViewAsync(Ct))!;
        Assert.Equal(response.StatusCode == HttpStatusCode.Created ? 1 : 0, view.McpTokens.Count);
        Assert.Equal((await Service(host).LoadAggregateAsync(Ct)).Version, view.Version);
    }

    [Fact]
    public async Task Parallel_creates_at_19_tokens_end_with_at_most_20_and_a_defined_outcome()
    {
        await using var host = await StartSignedInAsync(pg);
        for (var i = 0; i < 19; i++)
        {
            await CreateAsync(host, "t" + i);
        }

        var responses = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => host.SendAsync(HttpMethod.Post, Tokens, new { name = "parallel" + i })));

        try
        {
            Assert.All(responses, r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.Conflict, HttpStatusCode.PreconditionFailed }));
            Assert.Contains(responses, r => r.StatusCode == HttpStatusCode.Created);
            Assert.Equal(20, (await Service(host).GetViewAsync(Ct))!.McpTokens.Count);
        }
        finally
        {
            foreach (var r in responses)
            {
                r.Dispose();
            }
        }
    }

    [Fact]
    public async Task With_the_flag_off_create_is_mcp_disabled_and_appends_nothing_while_list_and_revoke_still_work()
    {
        await using var off = await StartSignedInAsync(pg, c => c["Mcp:Enabled"] = "false");
        var memberId = (await Service(off).GetViewAsync(Ct))!.Members.Single().MemberId;
        var (_, existing, _) = await Service(off).CreateMcpTokenAsync(memberId, "pre-existing", WriteContext.Member(memberId, null), Ct);
        var version = (await Service(off).GetViewAsync(Ct))!.Version;

        using var create = await off.SendAsync(HttpMethod.Post, Tokens, new { name = "blocked" });
        await ApiTestHost.AssertProblemAsync(create, HttpStatusCode.Conflict, "mcp-disabled");
        Assert.Equal(version, (await Service(off).GetViewAsync(Ct))!.Version);
        var list = await ListAsync(off);
        using var revoke = await off.SendAsync(HttpMethod.Delete, $"{Tokens}/{existing.Id}");

        Assert.Equal(existing.Id, Assert.Single(list.GetProperty("tokens").EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        Assert.Equal(version + 1, (await Service(off).GetViewAsync(Ct))!.Version); // only the revoke was appended
    }

    [Fact]
    public async Task No_log_line_contains_the_secret_its_hash_or_the_token_name()
    {
        await using var host = await StartSignedInAsync(pg);

        var created = await CreateAsync(host, "Very Distinctive Name");
        var secret = created.GetProperty("secret").GetString()!;
        var id = created.GetProperty("token").GetProperty("id").GetString()!;
        (await host.SendAsync(HttpMethod.Get, Tokens)).Dispose();
        (await host.SendAsync(HttpMethod.Delete, $"{Tokens}/{id}")).Dispose();
        using var bad = await host.SendAsync(HttpMethod.Post, Tokens, new { name = "" });

        Assert.Empty(host.Logs.Find(secret, McpTokenSecrets.Hash(secret), "Very Distinctive Name", "Authorization"));
        Assert.Contains(host.Logs.Entries, l => l.Contains("household.mcp_token_created " + id, StringComparison.Ordinal));
        Assert.Contains(host.Logs.Entries, l => l.Contains("household.mcp_token_revoked " + id, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_list_shows_the_last_used_time_from_the_use_document()
    {
        await using var host = await StartSignedInAsync(pg);
        var id = (await CreateAsync(host)).GetProperty("token").GetProperty("id").GetString()!;
        var tracker = host.App.Services.GetRequiredService<McpTokenUseTracker>();
        Assert.Equal(JsonValueKind.Null, (await ListAsync(host)).GetProperty("tokens")[0].GetProperty("lastUsedAt").ValueKind);

        await tracker.TouchAsync(id, Ct);
        await tracker.FlushAsync(Ct);

        var used = (await ListAsync(host)).GetProperty("tokens")[0].GetProperty("lastUsedAt").GetDateTimeOffset();
        Assert.True(Math.Abs((used - host.Time.GetUtcNow()).TotalSeconds) < 1);
    }
}
