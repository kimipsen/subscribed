using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Subscribed.Subscriptions;
using Subscribed.TestSupport;
using Xunit;

namespace Subscribed.Api.Tests;

public sealed class CatalogApiTests(PostgresFixture pg)
{
    private const string Url = "/api/subscriptions/catalog";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<ApiTestHost> MemberHostAsync(Catalog? catalog = null)
    {
        var host = await ApiTestHost.StartAsync(pg, services: catalog is null ? null : s => s.AddSingleton(catalog));
        await host.InitHouseholdAsync("subject-owner");
        (await host.LoginAsync("subject-owner")).Dispose();
        return host;
    }

    [Fact]
    public async Task Member_gets_the_catalog_in_file_order_without_source_urls()
    {
        await using var host = await MemberHostAsync(TestCatalog.Standard());
        using var response = await host.SendAsync(HttpMethod.Get, Url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var json = await ApiTestHost.JsonAsync(response);
        Assert.Equal("DK", json.GetProperty("defaultCountry").GetString());
        var services = json.GetProperty("services");
        Assert.Equal(["streamco", "music-box", "no-plans"], services.EnumerateArray().Select(s => s.GetProperty("id").GetString()));

        var first = services[0];
        Assert.Equal("StreamCo", first.GetProperty("name").GetString());
        Assert.Equal("/logos/streamco.svg", first.GetProperty("logoUrl").GetString());
        Assert.Equal("2026-11-01", first.GetProperty("lastVerified").GetString());
        Assert.False(first.TryGetProperty("sourceUrl", out _));
        var plans = first.GetProperty("plans");
        Assert.Equal(["Family", "Solo"], plans.EnumerateArray().Select(p => p.GetProperty("name").GetString()));
        var family = plans[0];
        Assert.Equal("129.00", family.GetProperty("amount").GetString());
        Assert.Equal(2, family.GetProperty("minorUnit").GetInt32());
        Assert.Equal("DKK", family.GetProperty("currency").GetString());
        Assert.Equal("month", family.GetProperty("cadenceUnit").GetString());
        Assert.Equal(1, family.GetProperty("cadenceCount").GetInt32());
        Assert.Equal("139.00", family.GetProperty("upcomingChange").GetProperty("amount").GetString());
        Assert.Equal("2027-02-01", family.GetProperty("upcomingChange").GetProperty("effectiveFrom").GetString());
        Assert.Equal(JsonValueKind.Null, plans[1].GetProperty("upcomingChange").ValueKind);
        Assert.Equal(JsonValueKind.Null, services[1].GetProperty("logoUrl").ValueKind);
        Assert.Equal(0, services[2].GetProperty("plans").GetArrayLength());
    }

    [Fact]
    public async Task Catalog_route_is_not_treated_as_a_subscription_id()
    {
        await using var host = await MemberHostAsync(TestCatalog.Standard());
        using var response = await host.SendAsync(HttpMethod.Get, Url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await ApiTestHost.JsonAsync(response)).TryGetProperty("services", out _));
    }

    [Fact]
    public async Task Shipped_catalog_is_served_by_the_real_host()
    {
        await using var host = await MemberHostAsync();
        using var response = await host.SendAsync(HttpMethod.Get, Url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var shipped = CatalogLoader.LoadEmbedded(TimeProvider.System);
        var json = await ApiTestHost.JsonAsync(response);
        Assert.Equal(shipped.Services.Count, json.GetProperty("services").GetArrayLength());
    }

    [Fact]
    public async Task Anonymous_request_gets_401()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        await host.InitHouseholdAsync("subject-owner");
        using var response = await host.SendAsync(HttpMethod.Get, Url);
        await ApiTestHost.AssertProblemAsync(response, HttpStatusCode.Unauthorized, "unauthenticated");
    }

    [Fact]
    public async Task Non_member_gets_403()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        await host.InitHouseholdAsync("subject-owner");
        (await host.LoginAsync("subject-stranger")).Dispose();
        using var response = await host.SendAsync(HttpMethod.Get, Url);
        await ApiTestHost.AssertProblemAsync(response, HttpStatusCode.Forbidden, "not-a-household-member");
    }

    [Fact]
    public async Task An_invalid_catalog_fails_host_startup_not_the_first_request()
    {
        var invalid = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("""{ "schemaVersion": 2, "services": [] }"""));
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => ApiTestHost.StartAsync(
            pg,
            services: s => s.AddSingleton<Catalog>(_ => CatalogLoader.Load(invalid, new DateOnly(2026, 11, 15)))));
        var root = ex is AggregateException agg ? agg.GetBaseException() : ex;
        Assert.IsType<CatalogValidationException>(root);
        Assert.Contains("schemaVersion", root.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Startup_logs_the_service_count_and_oldest_verification_date()
    {
        await using var host = await MemberHostAsync(TestCatalog.Standard());
        Assert.Contains(host.Logs.Entries, e => e.Contains("Catalog loaded: 3 services, oldest lastVerified 2026-10-01", StringComparison.Ordinal));
    }
}
