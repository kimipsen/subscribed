using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Subscribed.Subscriptions;
using Subscribed.TestSupport;
using Xunit;

namespace Subscribed.Api.Tests;

public sealed class CatalogLinkApiTests(PostgresFixture pg)
{
    private const string Base = "/api/subscriptions";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<ApiTestHost> StartAsync(TestDatabase db, Catalog catalog, bool initHousehold)
    {
        var host = await ApiTestHost.StartAsync(pg, database: db, services: s => s.AddSingleton(catalog));
        if (initHousehold)
        {
            await host.InitHouseholdAsync("subject-owner");
        }

        (await host.LoginAsync("subject-owner")).Dispose();
        return host;
    }

    private static object Body(ApiTestHost host, string? catalogId, string name = "StreamCo") =>
        new
        {
            name,
            category = (string?)null,
            notes = (string?)null,
            currency = "DKK",
            startDate = SubscriptionApiTests.Day(host, -100),
            endDate = (string?)null,
            amount = "129.00",
            cadenceUnit = "month",
            cadenceCount = 1,
            catalogId,
        };

    private static object PutBody(JsonElement detail, string? catalogId, string? name = null) => new
    {
        name = name ?? detail.GetProperty("name").GetString(),
        category = detail.GetProperty("category").GetString(),
        notes = detail.GetProperty("notes").GetString(),
        currency = detail.GetProperty("currency").GetString(),
        startDate = detail.GetProperty("startDate").GetString(),
        endDate = detail.GetProperty("endDate").GetString(),
        catalogId,
    };

    private static async Task<JsonElement> AddAsync(ApiTestHost host, string? catalogId, string name = "StreamCo")
    {
        using var response = await host.SendAsync(HttpMethod.Post, Base, Body(host, catalogId, name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ApiTestHost.JsonAsync(response);
    }

    [Fact]
    public async Task Add_with_a_known_catalog_id_returns_the_id_and_the_logo_url()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var host = await StartAsync(db, TestCatalog.Standard(), true);
        var created = await AddAsync(host, TestCatalog.StreamCo);
        Assert.Equal(TestCatalog.StreamCo, created.GetProperty("catalogId").GetString());
        Assert.Equal("/logos/streamco.svg", created.GetProperty("logoUrl").GetString());
        Assert.Equal(2, created.GetProperty("version").GetInt64());

        var detail = await SubscriptionApiTests.GetAsync(host, created.GetProperty("id").GetString()!);
        Assert.Equal(TestCatalog.StreamCo, detail.GetProperty("catalogId").GetString());

        var overview = await SubscriptionApiTests.OverviewAsync(host);
        var row = overview.GetProperty("subscriptions")[0];
        Assert.Equal(TestCatalog.StreamCo, row.GetProperty("catalogId").GetString());
        Assert.Equal("/logos/streamco.svg", row.GetProperty("logoUrl").GetString());
    }

    [Fact]
    public async Task Add_without_a_catalog_id_or_with_a_logo_less_entry_has_a_null_logo_url()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var host = await StartAsync(db, TestCatalog.Standard(), true);
        var free = await AddAsync(host, null);
        Assert.Equal(JsonValueKind.Null, free.GetProperty("catalogId").ValueKind);
        Assert.Equal(JsonValueKind.Null, free.GetProperty("logoUrl").ValueKind);
        Assert.Equal(1, free.GetProperty("version").GetInt64());

        var music = await AddAsync(host, TestCatalog.MusicBox, "Music");
        Assert.Equal(TestCatalog.MusicBox, music.GetProperty("catalogId").GetString());
        Assert.Equal(JsonValueKind.Null, music.GetProperty("logoUrl").ValueKind);
    }

    [Fact]
    public async Task Add_with_an_unknown_catalog_id_is_a_400_under_catalog_id_and_creates_nothing()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var host = await StartAsync(db, TestCatalog.Standard(), true);
        using var response = await host.SendAsync(HttpMethod.Post, Base, Body(host, "not-a-service"));
        var errors = await SubscriptionApiTests.ErrorsAsync(response);
        Assert.True(errors.TryGetProperty("catalogId", out _));
        Assert.Equal(0, (await SubscriptionApiTests.OverviewAsync(host)).GetProperty("subscriptions").GetArrayLength());
    }

    [Fact]
    public async Task A_badly_shaped_catalog_id_is_a_400_under_catalog_id()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var host = await StartAsync(db, TestCatalog.Standard(), true);
        using var response = await host.SendAsync(HttpMethod.Post, Base, Body(host, "Bad Id"));
        Assert.True((await SubscriptionApiTests.ErrorsAsync(response)).TryGetProperty("catalogId", out _));
    }

    [Fact]
    public async Task Update_links_relinks_and_unlinks_and_rejects_an_unknown_id()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var host = await StartAsync(db, TestCatalog.Standard(), true);
        var created = await AddAsync(host, null);
        var id = created.GetProperty("id").GetString()!;

        var linked = await SubscriptionApiTests.SendOkAsync(host, HttpMethod.Put, $"{Base}/{id}", PutBody(created, TestCatalog.StreamCo), SubscriptionApiTests.ETag(created));
        Assert.Equal(TestCatalog.StreamCo, linked.GetProperty("catalogId").GetString());
        Assert.Equal("/logos/streamco.svg", linked.GetProperty("logoUrl").GetString());

        using (var unknown = await host.SendAsync(HttpMethod.Put, $"{Base}/{id}", PutBody(linked, "nope"), SubscriptionApiTests.ETag(linked)))
        {
            Assert.True((await SubscriptionApiTests.ErrorsAsync(unknown)).TryGetProperty("catalogId", out _));
        }

        var relinked = await SubscriptionApiTests.SendOkAsync(host, HttpMethod.Put, $"{Base}/{id}", PutBody(linked, TestCatalog.MusicBox), SubscriptionApiTests.ETag(linked));
        Assert.Equal(TestCatalog.MusicBox, relinked.GetProperty("catalogId").GetString());

        var unlinked = await SubscriptionApiTests.SendOkAsync(host, HttpMethod.Put, $"{Base}/{id}", PutBody(relinked, null), SubscriptionApiTests.ETag(relinked));
        Assert.Equal(JsonValueKind.Null, unlinked.GetProperty("catalogId").ValueKind);
        Assert.Equal(JsonValueKind.Null, unlinked.GetProperty("logoUrl").ValueKind);
    }

    [Fact]
    public async Task Update_with_an_unknown_id_when_the_stored_id_is_null_is_400_not_500()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var host = await StartAsync(db, TestCatalog.Standard(), true);
        var created = await AddAsync(host, null);
        using var response = await host.SendAsync(HttpMethod.Put, $"{Base}/{created.GetProperty("id").GetString()}", PutBody(created, "nope"), SubscriptionApiTests.ETag(created));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await SubscriptionApiTests.ErrorsAsync(response)).TryGetProperty("catalogId", out _));
    }

    [Fact]
    public async Task Put_without_catalog_id_unlinks_a_linked_subscription()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var host = await StartAsync(db, TestCatalog.Standard(), true);
        var created = await AddAsync(host, TestCatalog.StreamCo);
        var body = new
        {
            name = "StreamCo",
            currency = "DKK",
            startDate = created.GetProperty("startDate").GetString(),
        };
        var updated = await SubscriptionApiTests.SendOkAsync(host, HttpMethod.Put, $"{Base}/{created.GetProperty("id").GetString()}", body, SubscriptionApiTests.ETag(created));
        Assert.Equal(JsonValueKind.Null, updated.GetProperty("catalogId").ValueKind);
    }

    [Fact]
    public async Task An_unlink_racing_with_another_edit_is_rejected_by_the_version_check()
    {
        await using var db = await pg.CreateDatabaseAsync();
        await using var host = await StartAsync(db, TestCatalog.Standard(), true);
        var created = await AddAsync(host, TestCatalog.StreamCo);
        var id = created.GetProperty("id").GetString()!;
        await SubscriptionApiTests.SendOkAsync(host, HttpMethod.Put, $"{Base}/{id}", PutBody(created, TestCatalog.StreamCo, "By A"), SubscriptionApiTests.ETag(created));

        using var stale = await host.SendAsync(HttpMethod.Put, $"{Base}/{id}", PutBody(created, null, "By B"), SubscriptionApiTests.ETag(created));
        await ApiTestHost.AssertProblemAsync(stale, HttpStatusCode.PreconditionFailed, "concurrency-conflict");
        Assert.Equal(TestCatalog.StreamCo, (await SubscriptionApiTests.GetAsync(host, id)).GetProperty("catalogId").GetString());
    }

    [Fact]
    public async Task A_removed_catalog_entry_keeps_reads_working_logs_the_dangling_id_and_still_allows_unchanged_saves()
    {
        await using var db = await pg.CreateDatabaseAsync();
        string id;
        JsonElement created;
        await using (var first = await StartAsync(db, TestCatalog.Standard(), true))
        {
            created = await AddAsync(first, TestCatalog.StreamCo);
            id = created.GetProperty("id").GetString()!;
        }

        await using var host = await StartAsync(db, TestCatalog.Without(TestCatalog.StreamCo), false);
        var detail = await SubscriptionApiTests.GetAsync(host, id);
        Assert.Equal(TestCatalog.StreamCo, detail.GetProperty("catalogId").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("logoUrl").ValueKind);
        var warning = Assert.Single(host.Logs.Entries, e => e.Contains("not in the catalog", StringComparison.Ordinal));
        Assert.Contains(id, warning, StringComparison.Ordinal);
        Assert.Contains(TestCatalog.StreamCo, warning, StringComparison.Ordinal);

        // Saving without touching the link keeps it, and a rename works.
        var saved = await SubscriptionApiTests.SendOkAsync(host, HttpMethod.Put, $"{Base}/{id}", PutBody(detail, TestCatalog.StreamCo, "Renamed"), SubscriptionApiTests.ETag(detail));
        Assert.Equal("Renamed", saved.GetProperty("name").GetString());
        Assert.Equal(TestCatalog.StreamCo, saved.GetProperty("catalogId").GetString());

        // Changing to a different unknown id is still rejected.
        using var changed = await host.SendAsync(HttpMethod.Put, $"{Base}/{id}", PutBody(saved, "other-gone"), SubscriptionApiTests.ETag(saved));
        Assert.Equal(HttpStatusCode.BadRequest, changed.StatusCode);
    }

    [Fact]
    public async Task The_overview_logs_one_warning_for_several_dangling_subscriptions()
    {
        await using var db = await pg.CreateDatabaseAsync();
        var ids = new List<string>();
        await using (var first = await StartAsync(db, TestCatalog.Standard(), true))
        {
            for (var i = 0; i < 7; i++)
            {
                ids.Add((await AddAsync(first, TestCatalog.StreamCo, $"Sub {i}")).GetProperty("id").GetString()!);
            }

            await AddAsync(first, TestCatalog.MusicBox, "Healthy");
        }

        await using var host = await StartAsync(db, TestCatalog.Without(TestCatalog.StreamCo), false);
        var overview = await SubscriptionApiTests.OverviewAsync(host);
        Assert.Equal(8, overview.GetProperty("subscriptions").GetArrayLength());
        Assert.All(overview.GetProperty("subscriptions").EnumerateArray().Where(r => r.GetProperty("catalogId").GetString() == TestCatalog.StreamCo),
            r => Assert.Equal(JsonValueKind.Null, r.GetProperty("logoUrl").ValueKind));

        var warning = Assert.Single(host.Logs.Entries, e => e.Contains("not in the catalog", StringComparison.Ordinal));
        Assert.Contains("7 subscriptions", warning, StringComparison.Ordinal);
        Assert.Contains(TestCatalog.StreamCo, warning, StringComparison.Ordinal);
        Assert.Equal(CatalogLog.MaxDanglingExamples, ids.Count(warning.Contains));
    }
}
