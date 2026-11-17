using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Subscribed.Household;
using Subscribed.TestSupport;
using Xunit;

namespace Subscribed.Api.Tests;

/// <summary>The anonymous iCal feed <c>/calendar/{secret}/subscribed.ics</c> (#33, plan 4.4): auth, the single 404, headers, secrecy.</summary>
public sealed partial class CalendarFeedEndpointTests(PostgresFixture pg)
{
    private const string Copenhagen = "Europe/Copenhagen";
    private const string RouteTemplate = "/calendar/{secret}/subscribed.ics";
    private static readonly DateTimeOffset Now = new(2030, 1, 15, 8, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex(@"^[A-Za-z0-9_-]{43}$")]
    private static partial Regex SecretShape();

    private sealed class EmptyTimeZoneSource : ITimeZoneSource
    {
        public IReadOnlyList<string> Ids => [];
    }

    private static string FeedPath(string secret) => $"/calendar/{secret}/subscribed.ics";

    private static string RandomSecret() => CalendarFeedSecrets.Create();

    /// <summary>A host with a signed-in owner, a fixed clock and, when asked, the household zone set.</summary>
    private async Task<ApiTestHost> StartAsync(string? zone = Copenhagen)
    {
        var host = await ApiTestHost.StartAsync(pg);
        host.Time.SetUtcNow(Now);
        await host.InitHouseholdAsync("subject-owner");
        (await host.LoginAsync("subject-owner")).Dispose();
        if (zone is not null)
        {
            using var get = await host.SendAsync(HttpMethod.Get, "/api/household");
            using var put = await host.SendAsync(HttpMethod.Put, "/api/household/time-zone", new { timeZoneId = zone }, get.Headers.ETag!.ToString());
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        }

        return host;
    }

    private static async Task<string> RotateAsync(ApiTestHost host)
    {
        using var get = await host.SendAsync(HttpMethod.Get, "/api/household");
        using var post = await host.SendAsync(HttpMethod.Post, "/api/household/calendar-feed", ifMatch: get.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        return (await ApiTestHost.JsonAsync(post)).GetProperty("feedPath").GetString()!.Split('/')[2];
    }

    private static async Task RevokeAsync(ApiTestHost host)
    {
        using var get = await host.SendAsync(HttpMethod.Get, "/api/household");
        using var delete = await host.SendAsync(HttpMethod.Delete, "/api/household/calendar-feed", ifMatch: get.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
    }

    private static async Task AddSubscriptionAsync(ApiTestHost host, string name, string cancelBy)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["category"] = null,
            ["notes"] = null,
            ["currency"] = "DKK",
            ["startDate"] = SubscriptionApiTests.Day(host, -100),
            ["endDate"] = null,
            ["amount"] = "129.00",
            ["cadenceUnit"] = "month",
            ["cadenceCount"] = 1,
            ["catalogId"] = null,
            ["cancelBy"] = cancelBy,
        };
        using var response = await host.SendAsync(HttpMethod.Post, "/api/subscriptions", body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>A client with no cookie jar and no X-CSRF: what a calendar app is.</summary>
    private static HttpClient Anonymous(ApiTestHost host) => host.App.GetTestServer().CreateClient();

    private static async Task<(HttpResponseMessage Response, string Body)> GetAsync(HttpClient client, string path, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, new Uri(path, UriKind.Relative));
        var response = await client.SendAsync(request, Ct);
        return (response, await response.Content.ReadAsStringAsync(Ct));
    }

    private static string WithoutTraceId(string problemBody)
    {
        using var doc = JsonDocument.Parse(problemBody);
        return JsonSerializer.Serialize(doc.RootElement.EnumerateObject().Where(p => p.Name != "traceId").ToDictionary(p => p.Name, p => p.Value.GetRawText()));
    }

    private static async Task AssertIdenticalNotFoundAsync(HttpResponseMessage response, string body)
    {
        await ApiTestHost.AssertProblemAsync(response, HttpStatusCode.NotFound, "not-found");
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.DoesNotContain("<html", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, response.Headers.Count(h => h.Key == "Set-Cookie"));
    }

    [Fact]
    public async Task The_right_secret_gives_200_with_no_cookie_and_no_csrf_header()
    {
        await using var host = await StartAsync();
        await AddSubscriptionAsync(host, "StreamCo", "2030-03-10T09:00");
        var secret = await RotateAsync(host);
        using var client = Anonymous(host);

        var (response, body) = await GetAsync(client, FeedPath(secret));
        using var _ = response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Matches(SecretShape(), secret);
        Assert.StartsWith("BEGIN:VCALENDAR", body, StringComparison.Ordinal);
        Assert.Contains("SUMMARY:Cancel StreamCo", body, StringComparison.Ordinal);
        Assert.Contains("X-WR-TIMEZONE:Europe/Copenhagen", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_feed_headers_are_text_calendar_no_store_noindex_and_inline()
    {
        await using var host = await StartAsync();
        var secret = await RotateAsync(host);
        using var client = Anonymous(host);

        var (response, _) = await GetAsync(client, FeedPath(secret));
        using var _1 = response;

        Assert.Equal("text/calendar", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("noindex", response.Headers.Single("X-Robots-Tag"));
        Assert.Equal("inline; filename=\"subscribed.ics\"", response.Content.Headers.ContentDisposition?.ToString());
        Assert.Equal("no-referrer", response.Headers.Single("Referrer-Policy"));
    }

    [Fact]
    public async Task Head_answers_200_with_the_same_headers_and_no_body()
    {
        await using var host = await StartAsync();
        var secret = await RotateAsync(host);
        using var client = Anonymous(host);

        var (response, body) = await GetAsync(client, FeedPath(secret), HttpMethod.Head);
        using var _ = response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/calendar", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("noindex", response.Headers.Single("X-Robots-Tag"));
        Assert.Empty(body);
    }

    [Fact]
    public async Task Wrong_revoked_malformed_and_rotated_out_secrets_give_the_identical_404()
    {
        await using var host = await StartAsync();
        var old = await RotateAsync(host);
        var current = await RotateAsync(host);
        var revoked = await RotateAsync(host);
        await RevokeAsync(host);
        using var client = Anonymous(host);

        var cases = new[]
        {
            ("wrong", RandomSecret()),
            ("revoked", revoked),
            ("rotated-out", old),
            ("rotated-out-2", current),
            ("too short", "abc"),
            ("too long", new string('a', 44)),
            ("bad characters", new string('a', 42) + "+"),
            ("padding", new string('a', 42) + "="),
        };
        var bodies = new List<string>();
        foreach (var (label, secret) in cases)
        {
            var (response, body) = await GetAsync(client, FeedPath(secret));
            using var _ = response;
            await AssertIdenticalNotFoundAsync(response, body);
            bodies.Add(WithoutTraceId(body));
            Assert.True(bodies.All(b => b == bodies[0]), $"{label}: body differs");
        }
    }

    [Fact]
    public async Task A_household_with_no_feed_answers_every_secret_with_the_same_404()
    {
        await using var host = await StartAsync();
        using var client = Anonymous(host);

        var (response, body) = await GetAsync(client, FeedPath(RandomSecret()));
        using var _ = response;

        await AssertIdenticalNotFoundAsync(response, body);
    }

    [Fact]
    public async Task Revoke_then_rotate_gives_a_new_working_url_and_the_revoked_one_stays_404()
    {
        await using var host = await StartAsync();
        var first = await RotateAsync(host);
        await RevokeAsync(host);
        var second = await RotateAsync(host);
        using var client = Anonymous(host);

        var (revoked, _) = await GetAsync(client, FeedPath(first));
        var (fresh, _) = await GetAsync(client, FeedPath(second));
        using var _1 = revoked;
        using var _2 = fresh;

        Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
    }

    [Fact]
    public async Task A_malformed_secret_does_not_query_the_database_but_a_well_formed_one_does()
    {
        await using var db = await pg.CreateDatabaseAsync();
        var unreachable = new Npgsql.NpgsqlConnectionStringBuilder(db.AppConnectionString) { Host = "127.0.0.1", Port = 1, Timeout = 2 }.ConnectionString;
        await using var host = await ApiTestHost.StartAsync(pg, c => c["ConnectionStrings:Subscribed"] = unreachable, db);
        using var client = Anonymous(host);

        var (malformed, malformedBody) = await GetAsync(client, FeedPath("not-a-secret"));
        var (wellFormed, _) = await GetAsync(client, FeedPath(RandomSecret()));
        using var _1 = malformed;
        using var _2 = wellFormed;

        await AssertIdenticalNotFoundAsync(malformed, malformedBody);
        await ApiTestHost.AssertProblemAsync(wellFormed, HttpStatusCode.ServiceUnavailable, "database-unavailable");
    }

    [Fact]
    public async Task The_feed_follows_a_household_time_zone_change_without_moving_the_instant()
    {
        await using var host = await StartAsync();
        await AddSubscriptionAsync(host, "StreamCo", "2030-03-10T09:00");
        var secret = await RotateAsync(host);
        using var client = Anonymous(host);

        var (before, copenhagen) = await GetAsync(client, FeedPath(secret));
        using var get = await host.SendAsync(HttpMethod.Get, "/api/household");
        using var put = await host.SendAsync(HttpMethod.Put, "/api/household/time-zone", new { timeZoneId = "Asia/Kolkata" }, get.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var (after, kolkata) = await GetAsync(client, FeedPath(secret));
        using var _1 = before;
        using var _2 = after;

        // 2030-03-10 09:00 Copenhagen (CET, +01:00) is 08:00 UTC, which is 13:30 in Kolkata (+05:30).
        Assert.Contains("DTSTART;TZID=Europe/Copenhagen:20300310T090000", copenhagen, StringComparison.Ordinal);
        Assert.Contains("DTSTART;TZID=Asia/Kolkata:20300310T133000", kolkata, StringComparison.Ordinal);
        Assert.Contains("X-WR-TIMEZONE:Asia/Kolkata", kolkata, StringComparison.Ordinal);
        Assert.DoesNotContain("Europe/Copenhagen", kolkata, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_lang_query_selects_the_language_and_an_unknown_one_is_english()
    {
        await using var host = await StartAsync();
        await AddSubscriptionAsync(host, "StreamCo", "2030-03-10T09:00");
        var secret = await RotateAsync(host);
        using var client = Anonymous(host);

        var (da, danish) = await GetAsync(client, FeedPath(secret) + "?lang=da");
        var (xx, unknown) = await GetAsync(client, FeedPath(secret) + "?lang=xx&lang=da");
        using var _1 = da;
        using var _2 = xx;

        Assert.Contains("SUMMARY:Opsig StreamCo", danish, StringComparison.Ordinal);
        Assert.Contains("SUMMARY:Cancel StreamCo", unknown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unresolvable_stored_zone_answers_in_utc_and_logs_a_warning_without_the_zone_id()
    {
        await using var host = await StartAsync(zone: null);
        await AddSubscriptionAsync(host, "StreamCo", "2030-03-10T09:00");
        var secret = await RotateAsync(host);
        // A zone the server cannot resolve, written straight into the view document.
        await host.Db.ExecuteAsMigratorAsync("UPDATE subscribed.mt_doc_household_view SET data = jsonb_set(data, '{timeZoneId}', '\"Mars/Olympus-Mons\"') WHERE id = 'household-default'");
        using var client = Anonymous(host);

        var (response, body) = await GetAsync(client, FeedPath(secret));
        using var _ = response;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("X-WR-TIMEZONE:UTC", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Olympus", body, StringComparison.Ordinal);
        Assert.Contains(host.Logs.Entries, e => e.StartsWith("Warning Subscribed.Household", StringComparison.Ordinal) && e.Contains("time_zone_unresolvable", StringComparison.Ordinal));
        Assert.Empty(host.Logs.Find("Olympus-Mons"));
    }

    [Fact]
    public async Task No_log_line_contains_the_secret_on_the_200_404_and_503_paths()
    {
        await using var host = await StartAsync();
        await AddSubscriptionAsync(host, "StreamCo", "2030-03-10T09:00");
        var secret = await RotateAsync(host);
        var wrong = RandomSecret();
        using var client = Anonymous(host);

        (await GetAsync(client, FeedPath(secret))).Response.Dispose();
        (await GetAsync(client, FeedPath(wrong))).Response.Dispose();
        (await GetAsync(client, FeedPath("short"))).Response.Dispose();
        await host.Db.ExecuteAsMigratorAsync($"REVOKE SELECT ON subscribed.mt_doc_household_view FROM {pg.AppRole}");
        var (failing, _) = await GetAsync(client, FeedPath(secret));
        using var _ = failing;

        Assert.True(failing.StatusCode is HttpStatusCode.InternalServerError or HttpStatusCode.ServiceUnavailable, failing.StatusCode.ToString());
        Assert.Empty(host.Logs.Find(secret, wrong, CalendarFeedSecrets.Hash(secret), CalendarFeedSecrets.Hash(wrong), "short"));
        Assert.DoesNotContain(host.Logs.Entries, e => e.Contains("/calendar/", StringComparison.Ordinal) && !e.Contains(RouteTemplate, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_request_log_carries_the_route_template_and_status_never_the_raw_path()
    {
        await using var host = await StartAsync();
        var secret = await RotateAsync(host);
        using var client = Anonymous(host);

        (await GetAsync(client, FeedPath(secret))).Response.Dispose();
        (await GetAsync(client, FeedPath(RandomSecret()))).Response.Dispose();
        (await GetAsync(client, FeedPath("short"))).Response.Dispose();

        var lines = host.Logs.Entries.Where(e => e.Contains("Subscribed.Requests", StringComparison.Ordinal) && e.Contains(RouteTemplate, StringComparison.Ordinal)).ToList();
        Assert.Equal(1, lines.Count(l => l.Contains("|StatusCode=200", StringComparison.Ordinal)));
        Assert.Equal(2, lines.Count(l => l.Contains("|StatusCode=404", StringComparison.Ordinal)));
        Assert.All(lines, l => Assert.DoesNotContain("subscribed.ics?", l, StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_feed_is_served_with_the_least_privilege_app_role()
    {
        await using var host = await StartAsync();
        var secret = await RotateAsync(host);
        using var client = Anonymous(host);

        // The host runs as the app role: with its read grant gone the feed cannot be answered, so it is not using another role.
        Assert.Equal(host.Db.AppConnectionString, host.Configuration["ConnectionStrings:Subscribed"]);
        var (ok, _) = await GetAsync(client, FeedPath(secret));
        using var _1 = ok;
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        await host.Db.ExecuteAsMigratorAsync($"REVOKE SELECT ON subscribed.mt_doc_household_view FROM {pg.AppRole}");
        var (denied, _) = await GetAsync(client, FeedPath(secret));
        using var _2 = denied;
        Assert.NotEqual(HttpStatusCode.OK, denied.StatusCode);
        Assert.NotEqual(HttpStatusCode.NotFound, denied.StatusCode);
    }

    [Fact]
    public async Task The_time_zones_list_comes_from_the_registered_source()
    {
        await using var host = await ApiTestHost.StartAsync(pg, services: s => s.AddSingleton<ITimeZoneSource>(new EmptyTimeZoneSource()));
        await host.InitHouseholdAsync("subject-owner");
        (await host.LoginAsync("subject-owner")).Dispose();

        using var response = await host.SendAsync(HttpMethod.Get, "/api/household/time-zones");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await ApiTestHost.JsonAsync(response)).GetProperty("timeZones").EnumerateArray());
    }
}
