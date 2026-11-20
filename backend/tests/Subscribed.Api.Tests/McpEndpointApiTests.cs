using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Subscribed.Api.Mcp;
using Subscribed.Household;
using Subscribed.Platform.EventStore;
using Subscribed.Platform.Identity;
using Subscribed.TestSupport;
using Xunit;

namespace Subscribed.Api.Tests;

/// <summary>
/// The <c>/mcp</c> endpoint over HTTP (#55, WP2): bearer authentication, revocation against the real projection, the body cap, the
/// kill switch, the read tools against the web endpoints, last-used and fault handling. Responses are read through a helper that accepts
/// both plain JSON and <c>text/event-stream</c> framing; the SDK in use answers with an event stream.
/// </summary>
public sealed class McpEndpointApiTests(PostgresFixture pg)
{
    /// <summary>The planned read tool names. The full allowlist of 20 adds the write tools (see <see cref="McpWriteToolsApiTests.AllToolNames"/>).</summary>
    internal static readonly string[] ReadToolNames =
    [
        "get_catalog", "get_payment_option_suggestions", "get_pauses", "get_price_history", "get_subscription",
        "get_subscription_options", "get_totals", "list_payment_options", "list_periods", "list_subscriptions",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string MemberIdOf(string subject) => MemberIdentity.MemberId(ApiTestHost.Issuer, subject);

    private sealed record Started(ApiTestHost Host, string Token, string TokenId);

    private async Task<Started> StartAsync(Action<Dictionary<string, string?>>? configure = null, Action<IServiceCollection>? services = null, bool createToken = true)
    {
        var host = await ApiTestHost.StartAsync(pg, configure, services: services);
        await host.InitHouseholdAsync("subject-a");
        (await host.LoginAsync("subject-a")).Dispose();
        if (!createToken)
        {
            return new Started(host, "", "");
        }

        using var response = await host.SendAsync(HttpMethod.Post, "/api/household/mcp-tokens", new { name = "Claude Code" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ApiTestHost.JsonAsync(response);
        return new Started(host, body.GetProperty("secret").GetString()!, body.GetProperty("token").GetProperty("id").GetString()!);
    }

    private static string WithoutTraceId(string body)
    {
        var node = JsonElement.Parse(body);
        return string.Join(",", node.EnumerateObject().Where(p => p.Name != "traceId").Select(p => p.Name + "=" + p.Value.GetRawText()));
    }

    private static async Task AssertUnauthenticatedAsync(McpReply reply)
    {
        await Task.CompletedTask;
        Assert.Equal(HttpStatusCode.Unauthorized, reply.Status);
        Assert.Equal("application/problem+json", reply.ContentType);
        Assert.Equal("Bearer", reply.Header("WWW-Authenticate"));
        Assert.Equal("urn:subscribed:problem:unauthenticated", JsonElement.Parse(reply.Raw).GetProperty("type").GetString());
    }

    // ---- tool list ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_valid_token_lists_exactly_the_planned_tools_and_the_read_tools_are_read_only()
    {
        var s = await StartAsync();
        await using var host = s.Host;

        var reply = await McpTestClient.ListToolsAsync(host, s.Token);

        Assert.Equal(HttpStatusCode.OK, reply.Status);
        var tools = reply.Result.GetProperty("tools").EnumerateArray().ToList();
        Assert.Equal(McpWriteToolsApiTests.AllToolNames.Order(StringComparer.Ordinal), tools.Select(t => t.GetProperty("name").GetString()!).Order(StringComparer.Ordinal));
        Assert.All(tools.Where(t => ReadToolNames.Contains(t.GetProperty("name").GetString()!)), t => Assert.True(t.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean()));
        Assert.All(tools.Where(t => !ReadToolNames.Contains(t.GetProperty("name").GetString()!)), t => Assert.False(t.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean()));
        Assert.Contains("version", tools.Single(t => t.GetProperty("name").GetString() == "get_subscription").GetProperty("description").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_in_the_mcp_namespace_references_SubscriptionService_RemoveAsync()
    {
        // BR-015: no MCP tool can delete a subscription. The scan resolves every call in the IL of every method (including async state machines).
        const System.Reflection.BindingFlags All = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;
        var types = typeof(McpLog).Assembly.GetTypes().Where(t => (t.FullName ?? "").StartsWith("Subscribed.Api.Mcp.", StringComparison.Ordinal)).ToList();
        var methods = types.SelectMany(t => t.GetMethods(All).Cast<System.Reflection.MethodBase>().Concat(t.GetConstructors(All))).ToList();

        static IEnumerable<System.Reflection.MethodBase> CalledFrom(System.Reflection.MethodBase method)
        {
            var il = method.GetMethodBody()?.GetILAsByteArray();
            if (il is null)
            {
                yield break;
            }

            var typeArgs = method.DeclaringType?.IsGenericType == true ? method.DeclaringType.GetGenericArguments() : null;
            var methodArgs = method.IsGenericMethod ? method.GetGenericArguments() : null;
            for (var i = 0; i + 4 < il.Length; i++)
            {
                if (il[i] is not (0x28 or 0x6F))
                {
                    continue;
                }

                System.Reflection.MethodBase? resolved;
                try
                {
                    resolved = method.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1), typeArgs, methodArgs);
                }
                catch (Exception ex) when (ex is ArgumentException or BadImageFormatException)
                {
                    continue;
                }

                if (resolved is not null)
                {
                    yield return resolved;
                }
            }
        }

        static bool Calls(System.Reflection.MethodBase method, string name) =>
            CalledFrom(method).Any(c => c.DeclaringType == typeof(Subscribed.Subscriptions.SubscriptionService) && c.Name == name);

        Assert.Contains(methods, m => Calls(m, "ListAsync"));
        Assert.DoesNotContain(methods, m => Calls(m, "RemoveAsync"));
    }

    // ---- authentication ------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("none")]
    [InlineData("malformed")]
    [InlineData("not-bearer")]
    [InlineData("unknown")]
    [InlineData("empty-bearer")]
    public async Task Every_credential_failure_is_the_same_401_with_a_bearer_challenge(string kind)
    {
        var s = await StartAsync();
        await using var host = s.Host;
        var unknown = McpTokenSecrets.Create();
        var authorization = kind switch
        {
            "none" => null,
            "malformed" => "Bearer not-a-token",
            "not-bearer" => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("a:b")),
            "unknown" => "Bearer " + unknown,
            _ => "Bearer ",
        };

        var baseline = await McpTestClient.SendAsync(host, HttpMethod.Post, "/mcp", null, McpTestClient.Rpc("tools/list"), useCookies: false);
        var reply = await McpTestClient.SendAsync(host, HttpMethod.Post, "/mcp", null, McpTestClient.Rpc("tools/list"), authorization, useCookies: false);

        await AssertUnauthenticatedAsync(reply);
        Assert.Equal(WithoutTraceId(baseline.Raw), WithoutTraceId(reply.Raw));
        Assert.DoesNotContain(unknown, reply.Raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_session_cookie_alone_never_reaches_mcp()
    {
        var s = await StartAsync();
        await using var host = s.Host;

        var reply = await McpTestClient.SendAsync(host, HttpMethod.Post, "/mcp", null, McpTestClient.Rpc("tools/list"));

        await AssertUnauthenticatedAsync(reply);
    }

    [Fact]
    public async Task A_cross_site_cookie_request_cannot_reach_mcp_and_mcp_is_outside_the_csrf_rules()
    {
        var s = await StartAsync();
        await using var host = s.Host;

        // The cookie request carries no X-CSRF header and a foreign Origin: on /api that is a 403, here the cookie is simply not a credential.
        var cross = await McpTestClient.SendAsync(host, HttpMethod.Post, "/mcp", null, McpTestClient.Rpc("tools/list"), configure: r => r.Headers.Add("Origin", "https://evil.example"));
        await AssertUnauthenticatedAsync(cross);

        // A token request needs no X-CSRF header either (it is not a browser credential).
        var withToken = await McpTestClient.ListToolsAsync(host, s.Token);
        Assert.Equal(HttpStatusCode.OK, withToken.Status);
        Assert.DoesNotContain(host.Jar.Responses, headers => headers.Any(h => h.StartsWith("Access-Control-Allow", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task An_mcp_token_on_a_web_endpoint_is_the_unchanged_web_401()
    {
        var s = await StartAsync();
        await using var host = s.Host;

        var reply = await McpTestClient.SendAsync(host, HttpMethod.Get, "/api/subscriptions", s.Token, configure: r => r.Headers.Add("X-CSRF", "1"), useCookies: false);

        Assert.Equal(HttpStatusCode.Unauthorized, reply.Status);
        Assert.Equal("urn:subscribed:problem:unauthenticated", JsonElement.Parse(reply.Raw).GetProperty("type").GetString());
        Assert.False(reply.HasHeader("WWW-Authenticate"));
    }

    [Fact]
    public async Task Web_endpoints_keep_their_401_and_403_bodies()
    {
        var s = await StartAsync();
        await using var host = s.Host;

        var anonymous = await McpTestClient.SendAsync(host, HttpMethod.Get, "/api/subscriptions", null, configure: r => r.Headers.Add("X-CSRF", "1"), useCookies: false);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.Status);
        var body = JsonElement.Parse(anonymous.Raw);
        Assert.Equal("urn:subscribed:problem:unauthenticated", body.GetProperty("type").GetString());
        Assert.Equal("Sign in to continue.", body.GetProperty("title").GetString());
        Assert.False(anonymous.HasHeader("WWW-Authenticate"));

        await SubscriptionApiTests.SwitchUserAsync(host, "subject-outsider");
        using var forbidden = await host.SendAsync(HttpMethod.Get, "/api/subscriptions");
        await ApiTestHost.AssertProblemAsync(forbidden, HttpStatusCode.Forbidden, "not-a-household-member");
        Assert.Equal("You are not a member of this household.", (await ApiTestHost.JsonAsync(forbidden)).GetProperty("title").GetString());
        Assert.False(forbidden.Headers.Contains("WWW-Authenticate"));
    }

    [Fact]
    public async Task A_database_failure_on_the_token_lookup_is_503_not_401()
    {
        var s = await StartAsync(services: sv => sv.AddSingleton<IHouseholdViewReader>(new ThrowingReader()));
        await using var host = s.Host;

        var reply = await McpTestClient.ListToolsAsync(host, s.Token);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, reply.Status);
        Assert.Equal("application/problem+json", reply.ContentType);
        Assert.Equal("urn:subscribed:problem:database-unavailable", JsonElement.Parse(reply.Raw).GetProperty("type").GetString());
    }

    private sealed class ThrowingReader : IHouseholdViewReader
    {
        public Task<HouseholdView?> GetAsync(CancellationToken ct = default) =>
            throw new EventStoreException(new EventStoreFailure(EventStoreFailureKind.DatabaseUnavailable), new InvalidOperationException("db down"));
    }

    // ---- revocation (real inline projection) ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_removed_members_token_stays_401_even_after_the_same_identity_is_granted_again()
    {
        var s = await StartAsync();
        await using var host = s.Host;
        var (grantExit, _, grantErr) = await host.AdminAsync("household", "grant", "--subject", "subject-b", "--label", "Member B");
        Assert.True(grantExit == 0, grantErr);
        await SubscriptionApiTests.SwitchUserAsync(host, "subject-b");
        var token = await McpTestClient.CreateTokenAsync(host, "B's agent");
        Assert.Equal(HttpStatusCode.OK, (await McpTestClient.ListToolsAsync(host, token)).Status);

        var (revokeExit, _, revokeErr) = await host.AdminAsync("household", "revoke", "--member-id", MemberIdOf("subject-b"));
        Assert.True(revokeExit == 0, revokeErr);
        await AssertUnauthenticatedAsync(await McpTestClient.ListToolsAsync(host, token));

        var (regrantExit, _, regrantErr) = await host.AdminAsync("household", "grant", "--subject", "subject-b", "--label", "Member B");
        Assert.True(regrantExit == 0, regrantErr);
        await AssertUnauthenticatedAsync(await McpTestClient.ListToolsAsync(host, token));
        Assert.Equal(HttpStatusCode.OK, (await McpTestClient.ListToolsAsync(host, s.Token)).Status);
    }

    [Fact]
    public async Task A_revoked_token_is_401_on_the_very_next_request()
    {
        var s = await StartAsync();
        await using var host = s.Host;
        Assert.Equal(HttpStatusCode.OK, (await McpTestClient.ListToolsAsync(host, s.Token)).Status);

        using (var revoke = await host.SendAsync(HttpMethod.Delete, "/api/household/mcp-tokens/" + s.TokenId))
        {
            Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        }

        await AssertUnauthenticatedAsync(await McpTestClient.ListToolsAsync(host, s.Token));
    }

    // ---- body cap -------------------------------------------------------------------------------------------------------------

    private sealed class UndeclaredLengthContent(string text) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) => await stream.WriteAsync(Encoding.UTF8.GetBytes(text), Ct);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    [Fact]
    public async Task A_body_over_64_KB_is_413_for_declared_and_chunked_bodies_and_no_tool_runs()
    {
        var s = await StartAsync();
        await using var host = s.Host;
        var huge = McpTestClient.Rpc("tools/call", new { name = "list_subscriptions", arguments = new { period = new string('a', 70_000) } });

        var declared = await McpTestClient.SendAsync(host, HttpMethod.Post, "/mcp", s.Token, huge);
        var chunked = await McpTestClient.SendAsync(host, HttpMethod.Post, "/mcp", s.Token, null, configure: r =>
        {
            r.Content = new UndeclaredLengthContent(huge);
            r.Content.Headers.ContentType = new("application/json");
        });

        foreach (var reply in new[] { declared, chunked })
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, reply.Status);
            Assert.Equal("application/problem+json", reply.ContentType);
            Assert.Equal("urn:subscribed:problem:payload-too-large", JsonElement.Parse(reply.Raw).GetProperty("type").GetString());
        }

        Assert.DoesNotContain(host.Logs.Entries, e => e.Contains("mcp-tool ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_body_just_under_the_cap_is_accepted()
    {
        var s = await StartAsync();
        await using var host = s.Host;

        var reply = await McpTestClient.CallAsync(host, s.Token, "list_subscriptions", new { period = new string('a', 60_000) });

        Assert.Equal(HttpStatusCode.OK, reply.Status);
        Assert.True(reply.Tool().IsError);
    }

    // ---- protocol versions ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("2025-11-25", "2025-11-25")]
    [InlineData("2025-06-18", "2025-06-18")]
    [InlineData("2024-11-05", "2024-11-05")]
    [InlineData("1999-01-01", "2025-11-25")]
    public async Task Initialize_negotiates_the_requested_revision_or_falls_back_to_the_newest_supported(string requested, string negotiated)
    {
        var s = await StartAsync();
        await using var host = s.Host;

        var reply = await McpTestClient.InitializeAsync(host, s.Token, requested);

        Assert.Equal(HttpStatusCode.OK, reply.Status);
        Assert.Equal(negotiated, reply.Result.GetProperty("protocolVersion").GetString());
        Assert.True(reply.Result.GetProperty("capabilities").TryGetProperty("tools", out _));
        Assert.False(reply.HasHeader("Mcp-Session-Id"));
    }

    // ---- GET / DELETE in stateless mode --------------------------------------------------------------------------------------

    [Theory]
    [InlineData("GET")]
    [InlineData("DELETE")]
    public async Task Get_and_delete_are_401_without_a_token_and_405_with_one(string method)
    {
        var s = await StartAsync();
        await using var host = s.Host;

        await AssertUnauthenticatedAsync(await McpTestClient.SendAsync(host, new HttpMethod(method), "/mcp", null, useCookies: false));
        var allowed = await McpTestClient.SendAsync(host, new HttpMethod(method), "/mcp", s.Token);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, allowed.Status);
        Assert.Equal("POST", allowed.Header("Allow"));
        Assert.Equal("application/problem+json", allowed.ContentType);
    }

    // ---- kill switch ----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("POST", "/mcp", true)]
    [InlineData("GET", "/mcp", true)]
    [InlineData("DELETE", "/mcp", true)]
    [InlineData("POST", "/mcp/x", true)]
    [InlineData("GET", "/mcp/x", true)]
    [InlineData("POST", "/mcp", false)]
    [InlineData("GET", "/mcp", false)]
    [InlineData("DELETE", "/mcp", false)]
    [InlineData("POST", "/mcp/x", false)]
    public async Task With_the_flag_off_every_method_on_mcp_is_a_plain_404_problem(string method, string path, bool withCookie)
    {
        var s = await StartAsync(c => c["Mcp:Enabled"] = "false", createToken: false);
        await using var host = s.Host;

        var reply = await McpTestClient.SendAsync(host, new HttpMethod(method), path, null, method == "POST" ? McpTestClient.Rpc("tools/list") : null, useCookies: withCookie);

        Assert.Equal(HttpStatusCode.NotFound, reply.Status);
        Assert.Equal("application/problem+json", reply.ContentType);
        Assert.Equal("urn:subscribed:problem:not-found", JsonElement.Parse(reply.Raw).GetProperty("type").GetString());
        Assert.False(reply.IsEventStream);
        Assert.DoesNotContain("jsonrpc", reply.Raw, StringComparison.Ordinal);

        using var create = await host.SendAsync(HttpMethod.Post, "/api/household/mcp-tokens", new { name = "x" });
        await ApiTestHost.AssertProblemAsync(create, HttpStatusCode.Conflict, "mcp-disabled");
    }

    [Theory]
    [InlineData("/.well-known/oauth-protected-resource")]
    [InlineData("/.well-known/oauth-authorization-server")]
    [InlineData("/.well-known/oauth-protected-resource/mcp")]
    [InlineData("/register")]
    public async Task The_oauth_discovery_probes_a_client_makes_after_the_401_are_a_clean_404_not_the_SPA(string path)
    {
        var s = await StartAsync();
        await using var host = s.Host;

        var reply = await McpTestClient.SendAsync(host, HttpMethod.Get, path, null, useCookies: false);

        Assert.Equal(HttpStatusCode.NotFound, reply.Status);
        Assert.Equal("application/problem+json", reply.ContentType);
    }

    // ---- read tools equal the web endpoints ---------------------------------------------------------------------------------

    [Fact]
    public async Task Every_read_tool_returns_the_same_json_as_the_web_endpoint()
    {
        var s = await StartAsync();
        await using var host = s.Host;
        using (var option = await host.SendAsync(HttpMethod.Post, "/api/payment-options", new { name = "Visa", label = "Visa 1234", brand = "visa" }))
        {
            Assert.Equal(HttpStatusCode.Created, option.StatusCode);
        }

        var created = await SubscriptionApiTests.AddAsync(host, SubscriptionApiTests.NewBody(host, "StreamCo", category: "Streaming", notes: "Family plan"));
        var id = created.GetProperty("id").GetString()!;
        using (var pause = await host.SendAsync(HttpMethod.Post, $"/api/subscriptions/{id}/pause", ifMatch: $"\"{created.GetProperty("version").GetInt64()}\""))
        {
            Assert.Equal(HttpStatusCode.OK, pause.StatusCode);
        }

        async Task<string> WebAsync(string path)
        {
            using var response = await host.SendAsync(HttpMethod.Get, path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadAsStringAsync(Ct);
        }

        async Task<string> ToolAsync(string tool, object? arguments = null)
        {
            var (isError, body) = (await McpTestClient.CallAsync(host, s.Token, tool, arguments)).Tool();
            Assert.False(isError, body.GetRawText());
            return body.GetRawText();
        }

        static string Canonical(string json) => JsonSerializer.Serialize(JsonElement.Parse(json));
        static string Slice(string json, params string[] path)
        {
            var node = JsonElement.Parse(json);
            foreach (var step in path)
            {
                node = node.GetProperty(step);
            }

            return node.GetRawText();
        }

        var overview = await WebAsync("/api/subscriptions");
        var detail = await WebAsync($"/api/subscriptions/{id}");
        Assert.Equal(Canonical(overview), Canonical(await ToolAsync("list_subscriptions")));
        Assert.Equal(Canonical(await WebAsync("/api/subscriptions?period=last-12-months")), Canonical(await ToolAsync("list_subscriptions", new { period = "last-12-months" })));
        Assert.Equal(Canonical(detail), Canonical(await ToolAsync("get_subscription", new { id })));
        Assert.Equal(Canonical(await WebAsync($"/api/subscriptions/{id}?period=year-2025")), Canonical(await ToolAsync("get_subscription", new { id, period = "year-2025" })));
        Assert.Equal(Canonical(Slice(detail, "priceHistory")), Canonical(await ToolAsync("get_price_history", new { id })));
        Assert.Equal(Canonical(Slice(detail, "pauseHistory")), Canonical(await ToolAsync("get_pauses", new { id })));
        Assert.Equal(Canonical(Slice(overview, "totals")), Canonical(await ToolAsync("get_totals")));
        Assert.Equal(Canonical(await WebAsync("/api/subscriptions/periods")), Canonical(await ToolAsync("list_periods")));
        Assert.Equal(Canonical(await WebAsync("/api/payment-options")), Canonical(await ToolAsync("list_payment_options")));
        Assert.Equal(Canonical(await WebAsync("/api/payment-options/suggestions")), Canonical(await ToolAsync("get_payment_option_suggestions")));
        Assert.Equal(Canonical(await WebAsync("/api/subscriptions/catalog")), Canonical(await ToolAsync("get_catalog")));
        Assert.Equal(Canonical(await WebAsync("/api/subscriptions/options")), Canonical(await ToolAsync("get_subscription_options")));
        Assert.Contains("\"paused\":true", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_bad_period_or_an_unknown_subscription_is_a_tool_error_with_the_web_api_code()
    {
        var s = await StartAsync();
        await using var host = s.Host;

        var (badPeriod, badBody) = (await McpTestClient.CallAsync(host, s.Token, "list_subscriptions", new { period = "decade" })).Tool();
        var (missing, missingBody) = (await McpTestClient.CallAsync(host, s.Token, "get_subscription", new { id = "does-not-exist" })).Tool();
        var (badPeriod2, _) = (await McpTestClient.CallAsync(host, s.Token, "get_totals", new { period = "year-1800" })).Tool();

        Assert.True(badPeriod);
        Assert.Equal("validation-failed", badBody.GetProperty("code").GetString());
        Assert.Equal(["period"], badBody.GetProperty("errors").EnumerateObject().Select(p => p.Name));
        Assert.True(missing);
        Assert.Equal("not-found", missingBody.GetProperty("code").GetString());
        Assert.True(badPeriod2);
    }

    // ---- last-used -----------------------------------------------------------------------------------------------------------

    private sealed class CountingStore : IMcpTokenUseStore
    {
        private int _writes;

        public int Writes => Volatile.Read(ref _writes);

        public Func<CancellationToken, Task>? Behavior { get; set; }

        public async Task UpsertAsync(string tokenId, DateTimeOffset lastUsedAt, CancellationToken ct)
        {
            if (Behavior is { } behavior)
            {
                await behavior(ct).ConfigureAwait(false);
            }

            Interlocked.Increment(ref _writes);
        }

        public Task<IReadOnlyDictionary<string, DateTimeOffset>> ListAsync(IReadOnlyCollection<string> tokenIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, DateTimeOffset>>(new Dictionary<string, DateTimeOffset>());
    }

    [Fact]
    public async Task Last_used_is_written_once_per_window_for_requests_over_mcp()
    {
        var store = new CountingStore();
        var s = await StartAsync(services: sv => sv.AddSingleton<IMcpTokenUseStore>(store));
        await using var host = s.Host;
        var tracker = host.App.Services.GetRequiredService<McpTokenUseTracker>();

        await McpTestClient.ListToolsAsync(host, s.Token);
        await McpTestClient.ListToolsAsync(host, s.Token);
        await tracker.FlushAsync(Ct);
        Assert.Equal(1, store.Writes);

        host.Time.Advance(McpTokenUseTracker.Window + TimeSpan.FromSeconds(1));
        await McpTestClient.ListToolsAsync(host, s.Token);
        await tracker.FlushAsync(Ct);
        Assert.Equal(2, store.Writes);
    }

    [Fact]
    public async Task A_failing_last_used_store_does_not_fail_the_request()
    {
        var store = new CountingStore { Behavior = _ => throw new InvalidOperationException("store down SECRET-STORE") };
        var s = await StartAsync(services: sv => sv.AddSingleton<IMcpTokenUseStore>(store));
        await using var host = s.Host;

        var reply = await McpTestClient.ListToolsAsync(host, s.Token);
        await host.App.Services.GetRequiredService<McpTokenUseTracker>().FlushAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, reply.Status);
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task A_never_completing_last_used_store_does_not_delay_the_request_and_is_cancelled_after_two_seconds()
    {
        var store = new CountingStore { Behavior = ct => Task.Delay(Timeout.Infinite, ct) };
        var s = await StartAsync(services: sv => sv.AddSingleton<IMcpTokenUseStore>(store));
        await using var host = s.Host;
        var tracker = host.App.Services.GetRequiredService<McpTokenUseTracker>();

        var reply = await McpTestClient.ListToolsAsync(host, s.Token);
        Assert.Equal(HttpStatusCode.OK, reply.Status);

        var flush = tracker.FlushAsync(Ct);
        Assert.False(flush.IsCompleted);
        host.Time.Advance(McpTokenUseTracker.Timeout + TimeSpan.FromSeconds(1));
        await flush.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(0, store.Writes);
    }

    // ---- unexpected exceptions and observability -------------------------------------------------------------------------

    private sealed class ThrowingFault(string tool) : IMcpToolFault
    {
        public void BeforeInvoke(string toolName)
        {
            if (toolName == tool)
            {
                throw new InvalidOperationException("SECRET-FAULT-MESSAGE with a stack");
            }
        }
    }

    [Fact]
    public async Task An_unexpected_exception_in_a_tool_is_a_generic_internal_error_with_one_message_free_failure_line()
    {
        var s = await StartAsync(services: sv => sv.AddSingleton<IMcpToolFault>(new ThrowingFault("list_subscriptions")));
        await using var host = s.Host;

        var reply = await McpTestClient.CallAsync(host, s.Token, "list_subscriptions");

        Assert.Equal(HttpStatusCode.OK, reply.Status);
        var (isError, body) = reply.Tool();
        Assert.True(isError);
        Assert.Equal("internal-error", body.GetProperty("code").GetString());
        Assert.Equal("The request failed.", body.GetProperty("title").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));
        Assert.Equal(["code", "title", "traceId"], body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.DoesNotContain("SECRET-FAULT-MESSAGE", reply.Raw, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", reply.Raw, StringComparison.Ordinal);
        Assert.Empty(host.Logs.Find("SECRET-FAULT-MESSAGE", "StackTrace", "   at Subscribed"));

        var failures = host.Logs.Entries.Where(e => e.Contains("mcp-tool list_subscriptions failure", StringComparison.Ordinal)).ToList();
        var line = Assert.Single(failures);
        Assert.Contains("InvalidOperationException", line, StringComparison.Ordinal);
        Assert.Contains(s.TokenId, line, StringComparison.Ordinal);
        Assert.Contains(body.GetProperty("traceId").GetString()!, line, StringComparison.Ordinal);
        Assert.DoesNotContain(host.Logs.Entries, e => e.Contains("mcp-tool list_subscriptions ok", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Each_tool_call_logs_one_content_free_outcome_line_and_each_401_class_one_line_and_no_secret_leaks()
    {
        var s = await StartAsync();
        await using var host = s.Host;
        var unknown = McpTokenSecrets.Create();

        await McpTestClient.CallAsync(host, s.Token, "list_periods");
        await McpTestClient.CallAsync(host, s.Token, "list_subscriptions", new { period = "SECRET-PERIOD-ARGUMENT" });
        await McpTestClient.SendAsync(host, HttpMethod.Post, "/mcp", null, McpTestClient.Rpc("tools/list"), useCookies: false);
        await McpTestClient.SendAsync(host, HttpMethod.Post, "/mcp", null, McpTestClient.Rpc("tools/list"), "Bearer short", useCookies: false);
        await McpTestClient.SendAsync(host, HttpMethod.Post, "/mcp", null, McpTestClient.Rpc("tools/list"), "Bearer " + unknown, useCookies: false);

        var entries = host.Logs.Entries;
        Assert.Single(entries, e => e.Contains("mcp-tool list_periods ok", StringComparison.Ordinal) && e.Contains(s.TokenId, StringComparison.Ordinal));
        Assert.Single(entries, e => e.Contains("mcp-tool list_subscriptions domain-error validation-failed", StringComparison.Ordinal));
        Assert.Single(entries, e => e.Contains("mcp-auth missing", StringComparison.Ordinal));
        Assert.Single(entries, e => e.Contains("mcp-auth malformed", StringComparison.Ordinal));
        Assert.Single(entries, e => e.Contains("mcp-auth unknown", StringComparison.Ordinal));
        Assert.All(entries.Where(e => e.Contains("mcp-tool ", StringComparison.Ordinal)), e => Assert.Matches("[0-9a-f-]{16,}", e));
        Assert.Empty(host.Logs.Find(s.Token, McpTokenSecrets.Hash(s.Token), unknown, McpTokenSecrets.Hash(unknown), "Authorization", "Bearer ", "SECRET-PERIOD-ARGUMENT"));
    }
}
