using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Subscribed.Subscriptions;
using Subscribed.TestSupport;
using Xunit;
using static Subscribed.Api.Tests.SubscriptionApiTests;

namespace Subscribed.Api.Tests;

/// <summary>
/// The ten MCP write tools (#55, WP3) against the real Postgres fixture. Each tool is compared with the web endpoint for the same change:
/// one subscription is driven through the web API and an identical one through the tools, and the detail DTOs must be equal apart from
/// generated ids and the <c>via</c> marker (which only the MCP side carries).
/// </summary>
public sealed class McpWriteToolsApiTests(PostgresFixture pg)
{
    private const string Subs = "/api/subscriptions";
    private const string Options = "/api/payment-options";

    internal static readonly string[] AllToolNames =
    [
        .. McpEndpointApiTests.ReadToolNames,
        "create_subscription", "update_subscription", "add_price_entry", "update_price_entry", "delete_price_entry",
        "pause_subscription", "resume_subscription", "add_pause_period", "update_pause_period", "delete_pause_period",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Started(ApiTestHost Host, string Token);

    private async Task<Started> StartAsync()
    {
        var host = await ApiTestHost.StartAsync(pg, services: sv => sv.AddSingleton(TestCatalog.Standard()));
        await host.InitHouseholdAsync("subject-owner");
        (await host.LoginAsync("subject-owner")).Dispose();

        // Keep clear of UTC midnight so "today" cannot change mid-test.
        var now = host.Time.GetUtcNow();
        if (now.TimeOfDay > TimeSpan.FromHours(23.9) || now.TimeOfDay < TimeSpan.FromMinutes(6))
        {
            host.Time.SetUtcNow(new DateTimeOffset(now.UtcDateTime.Date.AddHours(now.TimeOfDay < TimeSpan.FromHours(12) ? 12 : 36), TimeSpan.Zero));
        }

        return new Started(host, await McpTestClient.CreateTokenAsync(host));
    }

    private static async Task<(bool IsError, JsonElement Body)> ToolAsync(Started s, string tool, object? arguments) =>
        (await McpTestClient.CallAsync(s.Host, s.Token, tool, arguments)).Tool();

    private static async Task<JsonElement> ToolOkAsync(Started s, string tool, object? arguments)
    {
        var (isError, body) = await ToolAsync(s, tool, arguments);
        Assert.False(isError, body.GetRawText());
        return body;
    }

    private static Dictionary<string, object?> With(JsonElement detail, Dictionary<string, object?>? fields = null)
    {
        var args = new Dictionary<string, object?>
        {
            ["id"] = detail.GetProperty("id").GetString(),
            ["expectedVersion"] = detail.GetProperty("version").GetInt64(),
        };
        foreach (var (key, value) in fields ?? [])
        {
            args[key] = value;
        }

        return args;
    }

    /// <summary>The detail without generated ids and the via marker, so a web-made and an MCP-made subscription can be compared.</summary>
    private static string Normalize(JsonElement detail)
    {
        static void Strip(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject obj:
                    foreach (var key in new[] { "id", "entryId", "via" })
                    {
                        obj.Remove(key);
                    }

                    foreach (var child in obj.Select(p => p.Value).ToList())
                    {
                        Strip(child);
                    }

                    break;
                case JsonArray array:
                    foreach (var child in array)
                    {
                        Strip(child);
                    }

                    break;
            }
        }

        var parsed = JsonNode.Parse(detail.GetRawText());
        Strip(parsed);
        return parsed!.ToJsonString();
    }

    private static async Task<JsonElement> GetAsync(Started s, string id)
    {
        using var response = await s.Host.SendAsync(HttpMethod.Get, $"{Subs}/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiTestHost.JsonAsync(response);
    }

    private static Dictionary<string, object?> NewFields(ApiTestHost host, string? paymentOptionId = null) => new()
    {
        ["name"] = "StreamCo",
        ["category"] = "Streaming",
        ["notes"] = "Family plan",
        ["currency"] = "DKK",
        ["startDate"] = Day(host, -100),
        ["endDate"] = Day(host, 300),
        ["amount"] = "129.00",
        ["cadenceUnit"] = "month",
        ["cadenceCount"] = 1,
        ["catalogId"] = TestCatalog.StreamCo,
        ["unsubscribeUrl"] = "https://example.com/cancel",
        ["paymentOptionId"] = paymentOptionId,
        ["cancelBy"] = "2030-06-15T09:00",
    };

    private static async Task<JsonElement> CreateOptionAsync(ApiTestHost host, string name)
    {
        using var response = await host.SendAsync(HttpMethod.Post, Options, new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ApiTestHost.JsonAsync(response);
    }

    private static string[] ErrorKeys(JsonElement body) => [.. body.GetProperty("errors").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];

    // ---- allowlist ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_tool_list_is_exactly_the_twenty_planned_names_and_only_the_read_tools_are_read_only()
    {
        var s = await StartAsync();
        await using var host = s.Host;

        var reply = await McpTestClient.ListToolsAsync(host, s.Token);

        var tools = reply.Result.GetProperty("tools").EnumerateArray().ToList();
        Assert.Equal(20, AllToolNames.Length);
        Assert.Equal(AllToolNames.Order(StringComparer.Ordinal), tools.Select(t => t.GetProperty("name").GetString()!).Order(StringComparer.Ordinal));
        foreach (var tool in tools)
        {
            var name = tool.GetProperty("name").GetString()!;
            Assert.Equal(McpEndpointApiTests.ReadToolNames.Contains(name), tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        }

        Assert.DoesNotContain(tools, t => t.GetProperty("name").GetString()!.Contains("remove", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_existing_subscription_tool_requires_expected_version_and_create_does_not()
    {
        var s = await StartAsync();
        await using var host = s.Host;

        var tools = (await McpTestClient.ListToolsAsync(host, s.Token)).Result.GetProperty("tools").EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!);

        foreach (var name in new[] { "update_subscription", "add_price_entry", "update_price_entry", "delete_price_entry", "pause_subscription", "resume_subscription", "add_pause_period", "update_pause_period", "delete_pause_period" })
        {
            var required = tools[name].GetProperty("inputSchema").GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.Contains("expectedVersion", required);
            Assert.Contains("id", required);
        }

        Assert.DoesNotContain("expectedVersion", tools["create_subscription"].GetProperty("inputSchema").GetProperty("properties").EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task The_descriptions_carry_the_agent_instructions()
    {
        var s = await StartAsync();
        await using var host = s.Host;

        var tools = (await McpTestClient.ListToolsAsync(host, s.Token)).Result.GetProperty("tools").EnumerateArray().ToDictionary(t => t.GetProperty("name").GetString()!, t => t.GetProperty("description").GetString()!);

        Assert.StartsWith("This replaces all details. Call get_subscription first and send every field you want to keep; any omitted optional field is cleared.", tools["update_subscription"], StringComparison.Ordinal);
        Assert.DoesNotContain("unchanged", tools["update_subscription"].Replace("leave price entries alone", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NOT idempotent", tools["create_subscription"], StringComparison.Ordinal);
        Assert.Contains("list_subscriptions before retrying", tools["create_subscription"], StringComparison.Ordinal);
        foreach (var (name, description) in tools.Where(t => !McpEndpointApiTests.ReadToolNames.Contains(t.Key)))
        {
            Assert.Contains("re-read with get_subscription and retry", description, StringComparison.Ordinal);
            Assert.NotEmpty(name);
        }
    }

    // ---- each tool equals the web endpoint ----------------------------------------------------------------------------------

    [Fact]
    public async Task create_subscription_equals_the_web_create_and_its_first_price_row_shows_via_mcp()
    {
        var s = await StartAsync();
        await using var host = s.Host;
        var fields = NewFields(host);

        var web = await AddAsync(host, fields);
        var mcp = await ToolOkAsync(s, "create_subscription", fields);

        Assert.Equal(Normalize(web), Normalize(mcp));
        Assert.Equal("mcp", mcp.GetProperty("priceHistory")[0].GetProperty("via").GetString());
        Assert.Equal(JsonValueKind.Null, web.GetProperty("priceHistory")[0].GetProperty("via").ValueKind);
        Assert.Equal("mcp", (await GetAsync(s, mcp.GetProperty("id").GetString()!)).GetProperty("priceHistory")[0].GetProperty("via").GetString());
    }

    private sealed record Step(string Tool, HttpMethod Method, Func<JsonElement, string> Path, Func<JsonElement, Dictionary<string, object?>>? Fields);

    [Fact]
    public async Task Every_write_tool_returns_the_web_endpoints_detail_for_the_same_change()
    {
        var s = await StartAsync();
        await using var host = s.Host;
        var web = await AddAsync(host, NewFields(host));
        var mcp = await ToolOkAsync(s, "create_subscription", NewFields(host));
        Assert.Equal(Normalize(web), Normalize(mcp));

        Step[] steps =
        [
            new("update_subscription", HttpMethod.Put, d => $"{Subs}/{d.GetProperty("id").GetString()}", d => new()
            {
                ["name"] = "Renamed", ["category"] = "Video", ["notes"] = null, ["currency"] = "DKK", ["startDate"] = d.GetProperty("startDate").GetString(),
                ["endDate"] = null, ["catalogId"] = TestCatalog.MusicBox, ["unsubscribeUrl"] = null, ["cancelBy"] = null,
            }),
            new("add_price_entry", HttpMethod.Post, d => $"{Subs}/{d.GetProperty("id").GetString()}/prices", _ => new()
            {
                ["effectiveFrom"] = Day(host, -50), ["amount"] = "139.00", ["cadenceUnit"] = "month", ["cadenceCount"] = 1,
            }),
            new("update_price_entry", HttpMethod.Put, d => $"{Subs}/{d.GetProperty("id").GetString()}/prices/{d.GetProperty("priceHistory")[1].GetProperty("entryId").GetString()}", d => new()
            {
                ["entryId"] = d.GetProperty("priceHistory")[1].GetProperty("entryId").GetString(),
                ["effectiveFrom"] = Day(host, -40), ["amount"] = "140.00", ["cadenceUnit"] = "month", ["cadenceCount"] = 1,
            }),
            new("add_pause_period", HttpMethod.Post, d => $"{Subs}/{d.GetProperty("id").GetString()}/pauses", _ => new()
            {
                ["pauseDate"] = Day(host, -20), ["resumeDate"] = Day(host, -10),
            }),
            new("update_pause_period", HttpMethod.Put, d => $"{Subs}/{d.GetProperty("id").GetString()}/pauses/{d.GetProperty("pauseHistory")[0].GetProperty("id").GetString()}", d => new()
            {
                ["periodId"] = d.GetProperty("pauseHistory")[0].GetProperty("id").GetString(), ["pauseDate"] = Day(host, -21), ["resumeDate"] = Day(host, -10),
            }),
            new("delete_pause_period", HttpMethod.Delete, d => $"{Subs}/{d.GetProperty("id").GetString()}/pauses/{d.GetProperty("pauseHistory")[0].GetProperty("id").GetString()}", d => new()
            {
                ["periodId"] = d.GetProperty("pauseHistory")[0].GetProperty("id").GetString(),
            }),
            new("pause_subscription", HttpMethod.Post, d => $"{Subs}/{d.GetProperty("id").GetString()}/pause", null),
            new("resume_subscription", HttpMethod.Post, d => $"{Subs}/{d.GetProperty("id").GetString()}/resume", null),
            new("delete_price_entry", HttpMethod.Delete, d => $"{Subs}/{d.GetProperty("id").GetString()}/prices/{d.GetProperty("priceHistory")[1].GetProperty("entryId").GetString()}", d => new()
            {
                ["entryId"] = d.GetProperty("priceHistory")[1].GetProperty("entryId").GetString(),
            }),
        ];

        foreach (var step in steps)
        {
            // The web call carries the body fields without the path ids; DELETE and the plain POSTs carry none.
            var webBody = step.Method == HttpMethod.Delete || step.Fields is null
                ? null
                : step.Fields(web).Where(kv => kv.Key is not ("entryId" or "periodId")).ToDictionary(kv => kv.Key, kv => kv.Value);
            web = await SendOkAsync(host, step.Method, step.Path(web), webBody, ETag(web));
            mcp = await ToolOkAsync(s, step.Tool, With(mcp, step.Fields?.Invoke(mcp)));

            Assert.True(Normalize(web) == Normalize(mcp), $"{step.Tool}: web {Normalize(web)} vs mcp {Normalize(mcp)}");
        }
    }

    [Fact]
    public async Task A_price_entry_and_a_pause_period_added_over_mcp_show_via_and_the_web_ones_do_not()
    {
        var s = await StartAsync();
        await using var host = s.Host;
        var created = await AddAsync(host, NewFields(host));
        var id = created.GetProperty("id").GetString()!;

        var viaWeb = await SendOkAsync(host, HttpMethod.Post, $"{Subs}/{id}/prices", new { effectiveFrom = Day(host, -50), amount = "139.00", cadenceUnit = "month", cadenceCount = 1 }, ETag(created));
        var viaMcp = await ToolOkAsync(s, "add_price_entry", With(viaWeb, new() { ["effectiveFrom"] = Day(host, -30), ["amount"] = "149.00", ["cadenceUnit"] = "month", ["cadenceCount"] = 1 }));
        var paused = await ToolOkAsync(s, "add_pause_period", With(viaMcp, new() { ["pauseDate"] = Day(host, -20), ["resumeDate"] = Day(host, -10) }));

        Assert.Equal(["null", "null", "mcp"], paused.GetProperty("priceHistory").EnumerateArray().Select(e => e.GetProperty("via").ValueKind == JsonValueKind.Null ? "null" : e.GetProperty("via").GetString()!).ToArray());
        Assert.Equal("mcp", Assert.Single(paused.GetProperty("pauseHistory").EnumerateArray()).GetProperty("via").GetString());

        // D10: a web edit of the MCP row clears its via.
        var entry = paused.GetProperty("priceHistory")[2].GetProperty("entryId").GetString();
        var edited = await SendOkAsync(host, HttpMethod.Put, $"{Subs}/{id}/prices/{entry}", new { effectiveFrom = Day(host, -30), amount = "150.00", cadenceUnit = "month", cadenceCount = 1 }, ETag(paused));
        Assert.Equal(JsonValueKind.Null, edited.GetProperty("priceHistory")[2].GetProperty("via").ValueKind);
    }

    // ---- update_subscription is a full replace, exactly like web PUT --------------------------------------------------------

    private static Dictionary<string, object?> Full(JsonElement d) => new()
    {
        ["name"] = d.GetProperty("name").GetString(),
        ["category"] = d.GetProperty("category").GetString(),
        ["notes"] = d.GetProperty("notes").GetString(),
        ["currency"] = d.GetProperty("currency").GetString(),
        ["startDate"] = d.GetProperty("startDate").GetString(),
        ["endDate"] = d.GetProperty("endDate").GetString(),
        ["catalogId"] = d.GetProperty("catalogId").GetString(),
        ["unsubscribeUrl"] = d.GetProperty("unsubscribeUrl").GetString(),
        ["cancelBy"] = d.GetProperty("cancelBy").GetString(),
    };

    [Fact]
    public async Task update_subscription_matches_web_put_for_every_omitted_null_and_payment_option_case()
    {
        var s = await StartAsync();
        await using var host = s.Host;
        var option = await CreateOptionAsync(host, "Visa");
        var other = await CreateOptionAsync(host, "Mastercard");
        var optionId = option.GetProperty("id").GetString()!;
        var otherId = other.GetProperty("id").GetString()!;

        (string Name, Action<Dictionary<string, object?>> Change)[] cases =
        [
            ("all fields, nothing changed", _ => { }),
            ("one changed", f => f["name"] = "Renamed"),
            ("endDate omitted", f => f.Remove("endDate")),
            ("endDate null", f => f["endDate"] = null),
            ("catalogId omitted", f => f.Remove("catalogId")),
            ("catalogId null", f => f["catalogId"] = null),
            ("unsubscribeUrl omitted", f => f.Remove("unsubscribeUrl")),
            ("unsubscribeUrl null", f => f["unsubscribeUrl"] = null),
            ("cancelBy omitted", f => f.Remove("cancelBy")),
            ("cancelBy null", f => f["cancelBy"] = null),
            ("paymentOption omitted", f => f.Remove("paymentOption")),
            ("paymentOption null", f => f["paymentOption"] = null),
            ("paymentOption { id: null }", f => f["paymentOption"] = new Dictionary<string, object?> { ["id"] = null }),
            ("paymentOption {}", f => f["paymentOption"] = new Dictionary<string, object?>()),
            ("paymentOption set", f => f["paymentOption"] = new Dictionary<string, object?> { ["id"] = otherId }),
            ("paymentOption bad id", f => f["paymentOption"] = new Dictionary<string, object?> { ["id"] = "not-an-option" }),
        ];

        foreach (var (name, change) in cases)
        {
            var web = await AddAsync(host, NewFields(host, optionId));
            var mcp = await ToolOkAsync(s, "create_subscription", NewFields(host, optionId));
            Assert.Equal(Normalize(web), Normalize(mcp));

            var fields = Full(web);
            change(fields);

            using var response = await host.SendAsync(HttpMethod.Put, $"{Subs}/{web.GetProperty("id").GetString()}", fields, ETag(web));
            var (isError, body) = await ToolAsync(s, "update_subscription", With(mcp, fields));

            if (response.StatusCode == HttpStatusCode.OK)
            {
                Assert.False(isError, $"{name}: {body.GetRawText()}");
                Assert.True(Normalize(await ApiTestHost.JsonAsync(response)) == Normalize(body), name);
            }
            else
            {
                Assert.True(isError, name);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.Equal("validation-failed", body.GetProperty("code").GetString());
                Assert.Equal(ErrorKeys(await ApiTestHost.JsonAsync(response)), ErrorKeys(body));
            }
        }
    }

    [Fact]
    public async Task update_subscription_with_an_empty_payment_option_object_is_validation_failed_on_payment_option()
    {
        var s = await StartAsync();
        await using var host = s.Host;
        var created = await ToolOkAsync(s, "create_subscription", NewFields(host));

        var (isError, body) = await ToolAsync(s, "update_subscription", With(created, new Dictionary<string, object?>(Full(created)) { ["paymentOption"] = new Dictionary<string, object?>() }));

        Assert.True(isError);
        Assert.Equal("validation-failed", body.GetProperty("code").GetString());
        Assert.Equal(["paymentOption"], ErrorKeys(body));
        Assert.Equal(created.GetProperty("version").GetInt64(), (await GetAsync(s, created.GetProperty("id").GetString()!)).GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task update_subscription_with_an_unknown_payment_option_id_is_validation_failed()
    {
        var s = await StartAsync();
        await using var host = s.Host;
        var created = await ToolOkAsync(s, "create_subscription", NewFields(host));

        var (isError, body) = await ToolAsync(s, "update_subscription", With(created, new Dictionary<string, object?>(Full(created)) { ["paymentOption"] = new Dictionary<string, object?> { ["id"] = "not-an-option" } }));

        Assert.True(isError);
        Assert.Equal("validation-failed", body.GetProperty("code").GetString());
        Assert.Contains("paymentOptionId", ErrorKeys(body));
    }

    // ---- errors -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_invalid_currency_is_validation_failed_on_currency()
    {
        var s = await StartAsync();
        await using var host = s.Host;
        var fields = NewFields(host);
        fields["currency"] = "ZZZ";

        var (isError, body) = await ToolAsync(s, "create_subscription", fields);

        Assert.True(isError);
        Assert.Equal("validation-failed", body.GetProperty("code").GetString());
        Assert.Contains("currency", ErrorKeys(body));
    }

    [Fact]
    public async Task An_unavailable_payment_option_is_validation_failed_on_payment_option_id_for_create_and_the_price_entry_tools()
    {
        var s = await StartAsync();
        await using var host = s.Host;

        var fields = NewFields(host, "does-not-exist");
        var (createError, createBody) = await ToolAsync(s, "create_subscription", fields);
        Assert.True(createError);
        Assert.Equal(["paymentOptionId"], ErrorKeys(createBody));

        var created = await ToolOkAsync(s, "create_subscription", NewFields(host));
        var (addError, addBody) = await ToolAsync(s, "add_price_entry", With(created, new() { ["effectiveFrom"] = Day(host, -50), ["amount"] = "139.00", ["cadenceUnit"] = "month", ["cadenceCount"] = 1, ["paymentOptionId"] = "does-not-exist" }));
        Assert.True(addError);
        Assert.Equal(["paymentOptionId"], ErrorKeys(addBody));

        var (updateError, updateBody) = await ToolAsync(s, "update_price_entry", With(created, new()
        {
            ["entryId"] = created.GetProperty("priceHistory")[0].GetProperty("entryId").GetString(), ["effectiveFrom"] = created.GetProperty("startDate").GetString(),
            ["amount"] = "129.00", ["cadenceUnit"] = "month", ["cadenceCount"] = 1, ["paymentOptionId"] = "does-not-exist",
        }));
        Assert.True(updateError);
        Assert.Equal(["paymentOptionId"], ErrorKeys(updateBody));
        Assert.Equal(created.GetProperty("version").GetInt64(), (await GetAsync(s, created.GetProperty("id").GetString()!)).GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task A_stale_expected_version_is_concurrency_conflict_with_the_stored_version_and_appends_nothing()
    {
        var s = await StartAsync();
        await using var host = s.Host;
        var created = await ToolOkAsync(s, "create_subscription", NewFields(host));
        var id = created.GetProperty("id").GetString()!;
        var current = await ToolOkAsync(s, "update_subscription", With(created, new Dictionary<string, object?>(Full(created)) { ["name"] = "Second" }));

        // A multi-event update (details and a currency correction) with the stale version.
        var stale = new Dictionary<string, object?>(Full(created)) { ["name"] = "Third", ["currency"] = "EUR" };
        var (isError, body) = await ToolAsync(s, "update_subscription", With(created, stale));

        Assert.True(isError);
        Assert.Equal("concurrency-conflict", body.GetProperty("code").GetString());
        Assert.Equal(current.GetProperty("version").GetInt64(), body.GetProperty("currentVersion").GetInt64());
        var after = await GetAsync(s, id);
        Assert.Equal(current.GetProperty("version").GetInt64(), after.GetProperty("version").GetInt64());
        Assert.Equal("Second", after.GetProperty("name").GetString());
        Assert.Equal("DKK", after.GetProperty("currency").GetString());

        foreach (var (tool, extra) in new (string, Dictionary<string, object?>)[]
        {
            ("add_price_entry", new() { ["effectiveFrom"] = Day(host, -50), ["amount"] = "139.00", ["cadenceUnit"] = "month", ["cadenceCount"] = 1 }),
            ("pause_subscription", []),
            ("add_pause_period", new() { ["pauseDate"] = Day(host, -20) }),
        })
        {
            var (staleError, staleBody) = await ToolAsync(s, tool, With(created, extra));
            Assert.True(staleError, tool);
            Assert.Equal("concurrency-conflict", staleBody.GetProperty("code").GetString());
            Assert.Equal(after.GetProperty("version").GetInt64(), staleBody.GetProperty("currentVersion").GetInt64());
        }

        Assert.Equal(after.GetProperty("version").GetInt64(), (await GetAsync(s, id)).GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task Pausing_a_paused_subscription_and_resuming_one_that_is_not_paused_are_invalid_subscription_state_with_the_current_version()
    {
        var s = await StartAsync();
        await using var host = s.Host;
        var created = await ToolOkAsync(s, "create_subscription", NewFields(host));

        var (resumeError, resumeBody) = await ToolAsync(s, "resume_subscription", With(created));
        Assert.True(resumeError);
        Assert.Equal("invalid-subscription-state", resumeBody.GetProperty("code").GetString());
        Assert.Equal(created.GetProperty("version").GetInt64(), resumeBody.GetProperty("currentVersion").GetInt64());

        var paused = await ToolOkAsync(s, "pause_subscription", With(created));
        var (pauseError, pauseBody) = await ToolAsync(s, "pause_subscription", With(paused));
        Assert.True(pauseError);
        Assert.Equal("invalid-subscription-state", pauseBody.GetProperty("code").GetString());
        Assert.Equal(paused.GetProperty("version").GetInt64(), pauseBody.GetProperty("currentVersion").GetInt64());
    }

    [Fact]
    public async Task A_malformed_or_unknown_id_is_not_found_for_every_existing_subscription_tool()
    {
        var s = await StartAsync();
        await using var host = s.Host;
        var created = await ToolOkAsync(s, "create_subscription", NewFields(host));
        var real = created.GetProperty("id").GetString()!;

        async Task AssertNotFoundAsync(string tool, Dictionary<string, object?> args)
        {
            var (isError, body) = await ToolAsync(s, tool, args);
            Assert.True(isError, tool);
            Assert.Equal("not-found", body.GetProperty("code").GetString());
        }

        foreach (var bad in new[] { "not a real id", "00000000000000000000000000000000" })
        {
            await AssertNotFoundAsync("pause_subscription", new() { ["id"] = bad, ["expectedVersion"] = 1 });
            await AssertNotFoundAsync("delete_price_entry", new() { ["id"] = bad, ["expectedVersion"] = 1, ["entryId"] = bad });
        }

        await AssertNotFoundAsync("delete_price_entry", With(created, new() { ["entryId"] = "not a real id" }));
        await AssertNotFoundAsync("update_pause_period", With(created, new() { ["periodId"] = "not a real id", ["pauseDate"] = Day(host, -5) }));
        await AssertNotFoundAsync("delete_pause_period", With(created, new() { ["periodId"] = "00000000000000000000000000000000" }));
        Assert.Equal(created.GetProperty("version").GetInt64(), (await GetAsync(s, real)).GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task Two_parallel_calls_with_the_same_expected_version_yield_one_success_one_conflict_and_only_the_winners_events()
    {
        var s = await StartAsync();
        await using var host = s.Host;
        var created = await ToolOkAsync(s, "create_subscription", NewFields(host));
        var id = created.GetProperty("id").GetString()!;

        object Price(string from, string amount) => With(created, new() { ["effectiveFrom"] = from, ["amount"] = amount, ["cadenceUnit"] = "month", ["cadenceCount"] = 1 });
        var results = await Task.WhenAll(
            ToolAsync(s, "add_price_entry", Price(Day(host, -50), "139.00")),
            ToolAsync(s, "add_price_entry", Price(Day(host, -40), "149.00")));

        var winners = results.Where(r => !r.IsError).ToList();
        var losers = results.Where(r => r.IsError).ToList();
        Assert.Single(winners);
        var loser = Assert.Single(losers);
        Assert.Equal("concurrency-conflict", loser.Body.GetProperty("code").GetString());
        Assert.Equal(created.GetProperty("version").GetInt64() + 1, loser.Body.GetProperty("currentVersion").GetInt64());

        var stored = await GetAsync(s, id);
        Assert.Equal(created.GetProperty("version").GetInt64() + 1, stored.GetProperty("version").GetInt64());
        var amounts = stored.GetProperty("priceHistory").EnumerateArray().Select(e => e.GetProperty("amount").GetString()).ToList();
        Assert.Equal(2, amounts.Count);
        Assert.Equal(winners[0].Body.GetProperty("priceHistory").EnumerateArray().Select(e => e.GetProperty("amount").GetString()), amounts);
    }

    [Fact]
    public async Task No_write_tool_can_delete_a_subscription()
    {
        var s = await StartAsync();
        await using var host = s.Host;
        var created = await ToolOkAsync(s, "create_subscription", NewFields(host));
        var id = created.GetProperty("id").GetString()!;

        var reply = await McpTestClient.CallAsync(host, s.Token, "delete_subscription", With(created));

        Assert.True(reply.Message.TryGetProperty("error", out _) || reply.Tool().IsError);
        Assert.Equal(HttpStatusCode.OK, (await host.SendAsync(HttpMethod.Get, $"{Subs}/{id}")).StatusCode);
    }

    [Fact]
    public async Task An_mcp_write_stores_no_token_secret_and_logs_only_the_tool_outcome()
    {
        var s = await StartAsync();
        await using var host = s.Host;
        var created = await ToolOkAsync(s, "create_subscription", NewFields(host));
        _ = await ToolOkAsync(s, "pause_subscription", With(created));

        Assert.DoesNotContain(host.Logs.Entries, e => e.Contains(s.Token, StringComparison.Ordinal));
        Assert.Contains(host.Logs.Entries, e => e.Contains("mcp-tool pause_subscription", StringComparison.Ordinal));
    }
}
