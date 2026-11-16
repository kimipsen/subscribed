using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Subscribed.Subscriptions;
using Subscribed.TestSupport;
using Xunit;

namespace Subscribed.Api.Tests;

public sealed class UnsubscribeUrlApiTests(PostgresFixture pg)
{
    private const string Base = "/api/subscriptions";
    private const string UserUrl = "https://user.example/cancel";

    private async Task<ApiTestHost> StartAsync(TestDatabase db)
    {
        var host = await ApiTestHost.StartAsync(pg, database: db, services: s => s.AddSingleton(TestCatalog.Standard()));
        await host.InitHouseholdAsync("subject-owner");
        (await host.LoginAsync("subject-owner")).Dispose();
        return host;
    }

    private static Dictionary<string, object?> AddBody(ApiTestHost host, string? catalogId = null, string? url = null, bool includeUrl = true, string? end = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = "StreamCo",
            ["category"] = null,
            ["notes"] = null,
            ["currency"] = "DKK",
            ["startDate"] = SubscriptionApiTests.Day(host, -100),
            ["endDate"] = end,
            ["amount"] = "129.00",
            ["cadenceUnit"] = "month",
            ["cadenceCount"] = 1,
            ["catalogId"] = catalogId,
        };
        if (includeUrl)
        {
            body["unsubscribeUrl"] = url;
        }

        return body;
    }

    private static Dictionary<string, object?> PutBody(JsonElement detail, string? url, bool includeUrl = true, string? name = null, string? end = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = name ?? detail.GetProperty("name").GetString(),
            ["category"] = detail.GetProperty("category").GetString(),
            ["notes"] = detail.GetProperty("notes").GetString(),
            ["currency"] = detail.GetProperty("currency").GetString(),
            ["startDate"] = detail.GetProperty("startDate").GetString(),
            ["endDate"] = end ?? detail.GetProperty("endDate").GetString(),
            ["catalogId"] = detail.GetProperty("catalogId").GetString(),
        };
        if (includeUrl)
        {
            body["unsubscribeUrl"] = url;
        }

        return body;
    }

    private static async Task<JsonElement> CreateAsync(ApiTestHost host, object body)
    {
        using var response = await host.SendAsync(HttpMethod.Post, Base, body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ApiTestHost.JsonAsync(response);
    }

    private static Task<JsonElement> PutAsync(ApiTestHost host, JsonElement current, object body) =>
        SubscriptionApiTests.SendOkAsync(host, HttpMethod.Put, $"{Base}/{current.GetProperty("id").GetString()}", body, SubscriptionApiTests.ETag(current));

    private static string? Str(JsonElement e, string name) => e.GetProperty(name).ValueKind == JsonValueKind.Null ? null : e.GetProperty(name).GetString();

    private static async Task AssertRejectedAsync(HttpResponseMessage response, string value)
    {
        await ApiTestHost.AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation-failed");
        var json = await ApiTestHost.JsonAsync(response);
        Assert.True(json.GetProperty("errors").TryGetProperty("unsubscribeUrl", out _), json.GetRawText());
        Assert.DoesNotContain(value, json.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Add_with_a_user_url_echoes_it_and_resolves_it_as_the_effective_url()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var host = await StartAsync(db);
        var created = await CreateAsync(host, AddBody(host, url: UserUrl));
        Assert.Equal(UserUrl, Str(created, "unsubscribeUrl"));
        Assert.Equal(UserUrl, Str(created, "effectiveUnsubscribeUrl"));

        var detail = await SubscriptionApiTests.GetAsync(host, created.GetProperty("id").GetString()!);
        Assert.Equal(UserUrl, Str(detail, "unsubscribeUrl"));
        Assert.Equal(UserUrl, Str(detail, "effectiveUnsubscribeUrl"));
    }

    [Fact]
    public async Task The_url_is_stored_as_typed_after_trimming()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var host = await StartAsync(db);
        var created = await CreateAsync(host, AddBody(host, url: "  HTTPS://Example.com/Path?q=1#frag  "));
        Assert.Equal("HTTPS://Example.com/Path?q=1#frag", Str(created, "unsubscribeUrl"));
    }

    [Fact]
    public async Task Without_a_user_url_or_catalog_link_both_values_are_null()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var host = await StartAsync(db);
        var created = await CreateAsync(host, AddBody(host, includeUrl: false));
        Assert.Null(Str(created, "unsubscribeUrl"));
        Assert.Null(Str(created, "effectiveUnsubscribeUrl"));
    }

    [Fact]
    public async Task The_catalog_url_is_the_fallback_and_the_user_url_wins()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var host = await StartAsync(db);
        var linked = await CreateAsync(host, AddBody(host, TestCatalog.StreamCo));
        Assert.Null(Str(linked, "unsubscribeUrl"));
        Assert.Equal(TestCatalog.StreamCoUnsubscribeUrl, Str(linked, "effectiveUnsubscribeUrl"));

        var overridden = await PutAsync(host, linked, PutBody(linked, UserUrl));
        Assert.Equal(UserUrl, Str(overridden, "unsubscribeUrl"));
        Assert.Equal(UserUrl, Str(overridden, "effectiveUnsubscribeUrl"));

        var cleared = await PutAsync(host, overridden, PutBody(overridden, null));
        Assert.Null(Str(cleared, "unsubscribeUrl"));
        Assert.Equal(TestCatalog.StreamCoUnsubscribeUrl, Str(cleared, "effectiveUnsubscribeUrl"));
    }

    [Fact]
    public async Task A_service_without_a_catalog_url_resolves_to_none()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var host = await StartAsync(db);
        var created = await CreateAsync(host, AddBody(host, TestCatalog.MusicBox));
        Assert.Null(Str(created, "effectiveUnsubscribeUrl"));
    }

    [Fact]
    public async Task The_effective_url_is_shown_for_paused_and_ended_subscriptions()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var host = await StartAsync(db);
        var created = await CreateAsync(host, AddBody(host, TestCatalog.StreamCo));
        var paused = await SubscriptionApiTests.SendOkAsync(host, HttpMethod.Post, $"{Base}/{created.GetProperty("id").GetString()}/pause", null, SubscriptionApiTests.ETag(created));
        Assert.Equal("paused", paused.GetProperty("status").GetString());
        Assert.Equal(TestCatalog.StreamCoUnsubscribeUrl, Str(paused, "effectiveUnsubscribeUrl"));

        var resumed = await SubscriptionApiTests.SendOkAsync(host, HttpMethod.Post, $"{Base}/{created.GetProperty("id").GetString()}/resume", null, SubscriptionApiTests.ETag(paused));
        var ended = await PutAsync(host, resumed, PutBody(resumed, UserUrl, end: SubscriptionApiTests.Day(host, -1)));
        Assert.Equal("ended", ended.GetProperty("status").GetString());
        Assert.Equal(UserUrl, Str(ended, "effectiveUnsubscribeUrl"));
    }

    [Theory]
    [InlineData("http://example.com/cancel")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://u@a.example/x")]
    public async Task Add_and_update_reject_an_invalid_url_with_a_400_keyed_unsubscribe_url(string value)
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var host = await StartAsync(db);
        using (var add = await host.SendAsync(HttpMethod.Post, Base, AddBody(host, url: value)))
        {
            await AssertRejectedAsync(add, value);
        }

        var created = await CreateAsync(host, AddBody(host));
        using var update = await host.SendAsync(HttpMethod.Put, $"{Base}/{created.GetProperty("id").GetString()}", PutBody(created, value), SubscriptionApiTests.ETag(created));
        await AssertRejectedAsync(update, value);
    }

    [Theory]
    [InlineData("""{"name":"StreamCo","currency":"DKK","startDate":"2024-03-01","amount":"129.00","cadenceUnit":"month","cadenceCount":1,"unsubscribeUrl":42}""")]
    [InlineData("""{"name":"StreamCo","currency":"DKK","startDate":"2024-03-01","amount":"129.00","cadenceUnit":"month","cadenceCount":1,"unsubscribeUrl":{"a":1}}""")]
    public async Task A_non_string_url_is_a_400_keyed_unsubscribe_url_not_a_500(string raw)
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var host = await StartAsync(db);
        using var response = await host.SendAsync(HttpMethod.Post, Base, rawBody: raw);
        await ApiTestHost.AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation-failed");
        Assert.True((await ApiTestHost.JsonAsync(response)).GetProperty("errors").TryGetProperty("unsubscribeUrl", out _));
    }

    [Fact]
    public async Task An_update_that_omits_the_url_clears_it()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var host = await StartAsync(db);
        var created = await CreateAsync(host, AddBody(host, url: UserUrl));
        var updated = await PutAsync(host, created, PutBody(created, null, includeUrl: false, name: "Renamed"));
        Assert.Null(Str(updated, "unsubscribeUrl"));
        Assert.Null(Str(updated, "effectiveUnsubscribeUrl"));
    }

    [Fact]
    public async Task A_name_only_update_that_sends_the_url_keeps_it()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var host = await StartAsync(db);
        var created = await CreateAsync(host, AddBody(host, url: UserUrl));
        var updated = await PutAsync(host, created, PutBody(created, UserUrl, name: "Renamed"));
        Assert.Equal(UserUrl, Str(updated, "unsubscribeUrl"));
        var reread = await SubscriptionApiTests.GetAsync(host, created.GetProperty("id").GetString()!);
        Assert.Equal(UserUrl, Str(reread, "unsubscribeUrl"));
    }

    [Fact]
    public async Task The_overview_rows_are_unchanged_and_carry_no_url()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var host = await StartAsync(db);
        await CreateAsync(host, AddBody(host, url: UserUrl));
        var row = (await SubscriptionApiTests.OverviewAsync(host)).GetProperty("subscriptions")[0];
        Assert.False(row.TryGetProperty("unsubscribeUrl", out _));
        Assert.False(row.TryGetProperty("effectiveUnsubscribeUrl", out _));
    }

    [Fact]
    public async Task The_catalog_endpoint_exposes_unsubscribe_url_per_service()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var host = await StartAsync(db);
        using var response = await host.SendAsync(HttpMethod.Get, Base + "/catalog");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var services = (await ApiTestHost.JsonAsync(response)).GetProperty("services");
        Assert.Equal(TestCatalog.StreamCoUnsubscribeUrl, Str(services[0], "unsubscribeUrl"));
        Assert.Null(Str(services[1], "unsubscribeUrl"));
        Assert.Null(Str(services[2], "unsubscribeUrl"));
    }
}
