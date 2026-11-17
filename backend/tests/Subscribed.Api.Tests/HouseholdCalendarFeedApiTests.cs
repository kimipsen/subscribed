using System.Net;
using System.Text.RegularExpressions;
using Subscribed.Household;
using Subscribed.TestSupport;
using Xunit;

namespace Subscribed.Api.Tests;

/// <summary>Household time zone and calendar feed endpoints (#33): contracts, If-Match rules, secrecy and rotation.</summary>
public sealed partial class HouseholdCalendarFeedApiTests(PostgresFixture pg)
{
    private const string Household = "/api/household";
    private const string TimeZone = "/api/household/time-zone";
    private const string Feed = "/api/household/calendar-feed";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex(@"^/calendar/[A-Za-z0-9_-]{43}/subscribed\.ics$")]
    private static partial Regex FeedPathShape();

    private static async Task<ApiTestHost> StartSignedInAsync(PostgresFixture pg)
    {
        var host = await ApiTestHost.StartAsync(pg);
        await host.InitHouseholdAsync("subject-a");
        (await host.LoginAsync("subject-a")).Dispose();
        return host;
    }

    private static async Task<(string ETag, System.Text.Json.JsonElement Body)> GetHouseholdAsync(ApiTestHost host)
    {
        using var get = await host.SendAsync(HttpMethod.Get, Household);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        return (get.Headers.ETag!.ToString(), await ApiTestHost.JsonAsync(get));
    }

    private static async Task<string> RotateAsync(ApiTestHost host, string ifMatch)
    {
        using var response = await host.SendAsync(HttpMethod.Post, Feed, ifMatch: ifMatch);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ApiTestHost.JsonAsync(response)).GetProperty("feedPath").GetString()!;
    }

    private static string SecretOf(string feedPath) => feedPath.Split('/')[2];

    [Fact]
    public async Task Get_household_reports_utc_and_no_feed_for_an_existing_household()
    {
        await using var host = await StartSignedInAsync(pg);

        var (etag, body) = await GetHouseholdAsync(host);

        Assert.Equal("\"2\"", etag);
        Assert.Equal("UTC", body.GetProperty("timeZoneId").GetString());
        var feed = body.GetProperty("calendarFeed");
        Assert.Equal(["enabled", "rotatedAt"], feed.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.False(feed.GetProperty("enabled").GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, feed.GetProperty("rotatedAt").ValueKind);
        Assert.Equal(["calendarFeed", "id", "members", "name", "timeZoneId", "version"], body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Time_zones_lists_the_server_zones_without_posix_right_or_duplicates()
    {
        await using var host = await StartSignedInAsync(pg);

        using var response = await host.SendAsync(HttpMethod.Get, Household + "/time-zones");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var zones = (await ApiTestHost.JsonAsync(response)).GetProperty("timeZones").EnumerateArray().Select(z => z.GetString()!).ToList();
        Assert.Contains("Europe/Copenhagen", zones);
        Assert.Contains("UTC", zones);
        Assert.DoesNotContain(zones, z => z.StartsWith("posix/", StringComparison.Ordinal) || z.StartsWith("right/", StringComparison.Ordinal));
        Assert.Equal(zones.Count, zones.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(zones.Order(StringComparer.Ordinal), zones);
    }

    [Fact]
    public async Task Time_zones_requires_a_session()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        await host.InitHouseholdAsync("subject-a");

        using var response = await host.SendAsync(HttpMethod.Get, Household + "/time-zones");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Put_time_zone_returns_the_household_with_a_new_etag_and_get_shows_it()
    {
        await using var host = await StartSignedInAsync(pg);

        using var response = await host.SendAsync(HttpMethod.Put, TimeZone, new { timeZoneId = "Europe/Copenhagen" }, ifMatch: "\"2\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"3\"", response.Headers.ETag?.ToString());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await ApiTestHost.JsonAsync(response);
        Assert.Equal("Europe/Copenhagen", body.GetProperty("timeZoneId").GetString());
        Assert.Equal(3, body.GetProperty("version").GetInt64());
        var (etag, again) = await GetHouseholdAsync(host);
        Assert.Equal("\"3\"", etag);
        Assert.Equal("Europe/Copenhagen", again.GetProperty("timeZoneId").GetString());
    }

    [Fact]
    public async Task Put_the_current_time_zone_keeps_the_version()
    {
        await using var host = await StartSignedInAsync(pg);

        using var response = await host.SendAsync(HttpMethod.Put, TimeZone, new { timeZoneId = "UTC" }, ifMatch: "\"2\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"2\"", response.Headers.ETag?.ToString());
    }

    [Theory]
    [InlineData("{\"timeZoneId\":\"Mars/Olympus_Mons\"}")]
    [InlineData("{\"timeZoneId\":\"\"}")]
    [InlineData("{\"timeZoneId\":null}")]
    [InlineData("{}")]
    [InlineData("{\"timeZoneId\":\"../etc/passwd\"}")]
    [InlineData("{\"timeZoneId\":\"posix/Europe/Copenhagen\"}")]
    [InlineData("{\"timeZoneId\":\"Romance Standard Time\"}")]
    [InlineData("{\"timeZoneId\":5}")]
    [InlineData("{not json")]
    [InlineData("null")]
    public async Task Put_time_zone_rejects_an_unknown_or_malformed_zone_with_400_on_time_zone_id(string rawBody)
    {
        await using var host = await StartSignedInAsync(pg);

        using var response = await host.SendAsync(HttpMethod.Put, TimeZone, rawBody: rawBody, ifMatch: "\"2\"");

        await ApiTestHost.AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation-failed");
        var errors = (await ApiTestHost.JsonAsync(response)).GetProperty("errors");
        var only = Assert.Single(errors.EnumerateObject());
        Assert.Equal("timeZoneId", only.Name);
        Assert.Equal("Unknown time zone.", Assert.Single(only.Value.EnumerateArray()).GetString());
        Assert.Equal("UTC", (await GetHouseholdAsync(host)).Body.GetProperty("timeZoneId").GetString());
    }

    [Fact]
    public async Task Put_time_zone_missing_if_match_428_stale_412_missing_csrf_403()
    {
        await using var host = await StartSignedInAsync(pg);
        var body = new { timeZoneId = "Europe/Copenhagen" };

        using (var missing = await host.SendAsync(HttpMethod.Put, TimeZone, body))
        {
            await ApiTestHost.AssertProblemAsync(missing, HttpStatusCode.PreconditionRequired, "if-match-required");
        }

        using (var malformed = await host.SendAsync(HttpMethod.Put, TimeZone, body, ifMatch: "2"))
        {
            await ApiTestHost.AssertProblemAsync(malformed, HttpStatusCode.BadRequest, "validation-failed");
        }

        using (var noCsrf = await host.SendAsync(HttpMethod.Put, TimeZone, body, ifMatch: "\"2\"", csrf: false))
        {
            await ApiTestHost.AssertProblemAsync(noCsrf, HttpStatusCode.Forbidden, "csrf-header-required");
        }

        using (var stale = await host.SendAsync(HttpMethod.Put, TimeZone, body, ifMatch: "\"1\""))
        {
            await ApiTestHost.AssertProblemAsync(stale, HttpStatusCode.PreconditionFailed, "concurrency-conflict");
            Assert.Equal(2, (await ApiTestHost.JsonAsync(stale)).GetProperty("currentVersion").GetInt64());
        }

        Assert.Equal("UTC", (await GetHouseholdAsync(host)).Body.GetProperty("timeZoneId").GetString());
    }

    [Fact]
    public async Task Rotate_returns_the_path_once_and_the_household_then_shows_enabled()
    {
        await using var host = await StartSignedInAsync(pg);

        using var response = await host.SendAsync(HttpMethod.Post, Feed, ifMatch: "\"2\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"3\"", response.Headers.ETag?.ToString());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await ApiTestHost.JsonAsync(response);
        Assert.Equal(["feedPath", "household"], body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        var path = body.GetProperty("feedPath").GetString()!;
        Assert.Matches(FeedPathShape(), path);
        var inline = body.GetProperty("household").GetProperty("calendarFeed");
        Assert.True(inline.GetProperty("enabled").GetBoolean());
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, inline.GetProperty("rotatedAt").ValueKind);

        var (etag, household) = await GetHouseholdAsync(host);
        Assert.Equal("\"3\"", etag);
        Assert.True(household.GetProperty("calendarFeed").GetProperty("enabled").GetBoolean());
        var raw = household.GetRawText();
        Assert.DoesNotContain(SecretOf(path), raw, StringComparison.Ordinal);
        Assert.DoesNotContain(CalendarFeedSecrets.Hash(SecretOf(path)), raw, StringComparison.Ordinal);
        Assert.DoesNotContain("hash", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Rotate_if_match_rules_and_csrf()
    {
        await using var host = await StartSignedInAsync(pg);

        using (var missing = await host.SendAsync(HttpMethod.Post, Feed))
        {
            await ApiTestHost.AssertProblemAsync(missing, HttpStatusCode.PreconditionRequired, "if-match-required");
        }

        using (var noCsrf = await host.SendAsync(HttpMethod.Post, Feed, ifMatch: "\"2\"", csrf: false))
        {
            await ApiTestHost.AssertProblemAsync(noCsrf, HttpStatusCode.Forbidden, "csrf-header-required");
        }

        using (var stale = await host.SendAsync(HttpMethod.Post, Feed, ifMatch: "\"1\""))
        {
            await ApiTestHost.AssertProblemAsync(stale, HttpStatusCode.PreconditionFailed, "concurrency-conflict");
        }

        Assert.False((await GetHouseholdAsync(host)).Body.GetProperty("calendarFeed").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Rotate_twice_gives_a_different_path_each_time()
    {
        await using var host = await StartSignedInAsync(pg);

        var first = await RotateAsync(host, "\"2\"");
        var second = await RotateAsync(host, "\"3\"");

        Assert.NotEqual(first, second);
        Assert.Matches(FeedPathShape(), second);
        Assert.Equal("\"4\"", (await GetHouseholdAsync(host)).ETag);
    }

    [Fact]
    public async Task Two_concurrent_rotates_give_exactly_one_success_and_only_that_path_matches_the_stored_hash()
    {
        await using var host = await StartSignedInAsync(pg);

        var responses = await Task.WhenAll(
            host.SendAsync(HttpMethod.Post, Feed, ifMatch: "\"2\""),
            host.SendAsync(HttpMethod.Post, Feed, ifMatch: "\"2\""));
        try
        {
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.PreconditionFailed);
            var ok = responses.Single(r => r.StatusCode == HttpStatusCode.OK);
            var secret = SecretOf((await ApiTestHost.JsonAsync(ok)).GetProperty("feedPath").GetString()!);
            var stored = await host.Db.ScalarAsMigratorAsync<string>("SELECT data ->> 'calendarFeedSecretHash' FROM subscribed.mt_doc_household_view WHERE id = 'household-default'");
            Assert.True(CalendarFeedSecrets.Matches(secret, stored));
            Assert.Equal(1L, await host.Db.ScalarAsMigratorAsync<long>("SELECT count(*) FROM subscribed.mt_events WHERE type = 'household.calendar_feed_rotated.v1'"));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task Revoke_returns_the_household_without_a_feed_then_rotate_gives_a_new_link()
    {
        await using var host = await StartSignedInAsync(pg);
        var first = await RotateAsync(host, "\"2\"");

        using (var revoked = await host.SendAsync(HttpMethod.Delete, Feed, ifMatch: "\"3\""))
        {
            Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
            Assert.Equal("\"4\"", revoked.Headers.ETag?.ToString());
            Assert.Equal("no-store", revoked.Headers.CacheControl?.ToString());
            var feed = (await ApiTestHost.JsonAsync(revoked)).GetProperty("calendarFeed");
            Assert.False(feed.GetProperty("enabled").GetBoolean());
            Assert.Equal(System.Text.Json.JsonValueKind.Null, feed.GetProperty("rotatedAt").ValueKind);
        }

        var second = await RotateAsync(host, "\"4\"");
        Assert.NotEqual(first, second);
        Assert.True(CalendarFeedSecrets.Matches(SecretOf(second), await host.Db.ScalarAsMigratorAsync<string>("SELECT data ->> 'calendarFeedSecretHash' FROM subscribed.mt_doc_household_view WHERE id = 'household-default'")));
        Assert.False(CalendarFeedSecrets.Matches(SecretOf(first), await host.Db.ScalarAsMigratorAsync<string>("SELECT data ->> 'calendarFeedSecretHash' FROM subscribed.mt_doc_household_view WHERE id = 'household-default'")));
    }

    [Fact]
    public async Task Revoke_when_no_feed_exists_is_a_200_with_no_version_bump_and_rules_apply()
    {
        await using var host = await StartSignedInAsync(pg);

        using (var missing = await host.SendAsync(HttpMethod.Delete, Feed))
        {
            await ApiTestHost.AssertProblemAsync(missing, HttpStatusCode.PreconditionRequired, "if-match-required");
        }

        using (var noCsrf = await host.SendAsync(HttpMethod.Delete, Feed, ifMatch: "\"2\"", csrf: false))
        {
            await ApiTestHost.AssertProblemAsync(noCsrf, HttpStatusCode.Forbidden, "csrf-header-required");
        }

        using (var stale = await host.SendAsync(HttpMethod.Delete, Feed, ifMatch: "\"1\""))
        {
            await ApiTestHost.AssertProblemAsync(stale, HttpStatusCode.PreconditionFailed, "concurrency-conflict");
        }

        using var none = await host.SendAsync(HttpMethod.Delete, Feed, ifMatch: "\"2\"");
        Assert.Equal(HttpStatusCode.OK, none.StatusCode);
        Assert.Equal("\"2\"", none.Headers.ETag?.ToString());
    }

    [Fact]
    public async Task Stale_if_match_on_rotate_leaves_the_current_secret_in_place()
    {
        await using var host = await StartSignedInAsync(pg);
        var path = await RotateAsync(host, "\"2\"");
        var before = await host.Db.ScalarAsMigratorAsync<string>("SELECT data ->> 'calendarFeedSecretHash' FROM subscribed.mt_doc_household_view WHERE id = 'household-default'");

        using var stale = await host.SendAsync(HttpMethod.Post, Feed, ifMatch: "\"2\"");

        await ApiTestHost.AssertProblemAsync(stale, HttpStatusCode.PreconditionFailed, "concurrency-conflict");
        var after = await host.Db.ScalarAsMigratorAsync<string>("SELECT data ->> 'calendarFeedSecretHash' FROM subscribed.mt_doc_household_view WHERE id = 'household-default'");
        Assert.Equal(before, after);
        Assert.True(CalendarFeedSecrets.Matches(SecretOf(path), after));
    }

    [Fact]
    public async Task Raw_events_hold_only_the_hash_and_the_secret_never_reaches_a_log_line()
    {
        await using var host = await StartSignedInAsync(pg);
        var secret = SecretOf(await RotateAsync(host, "\"2\""));
        using (var revoked = await host.SendAsync(HttpMethod.Delete, Feed, ifMatch: "\"3\""))
        {
            Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        }

        var events = await host.Db.ScalarAsMigratorAsync<string>("SELECT string_agg(data::text || headers::text, ' ') FROM subscribed.mt_events");
        Assert.DoesNotContain(secret, events, StringComparison.Ordinal);
        Assert.Contains(CalendarFeedSecrets.Hash(secret), events, StringComparison.Ordinal);
        Assert.Empty(host.Logs.Find(secret, CalendarFeedSecrets.Hash(secret)));
    }

    [Fact]
    public async Task Rotate_and_revoke_each_write_one_information_line_with_the_household_id()
    {
        await using var host = await StartSignedInAsync(pg);
        var secret = SecretOf(await RotateAsync(host, "\"2\""));
        (await host.SendAsync(HttpMethod.Delete, Feed, ifMatch: "\"3\"")).Dispose();

        var entries = host.Logs.Entries;
        var rotated = Assert.Single(entries, e => e.Contains("household.calendar_feed_rotated", StringComparison.Ordinal));
        var revoked = Assert.Single(entries, e => e.Contains("household.calendar_feed_revoked", StringComparison.Ordinal));
        Assert.StartsWith("Information ", rotated, StringComparison.Ordinal);
        Assert.StartsWith("Information ", revoked, StringComparison.Ordinal);
        Assert.Contains("household-default", rotated, StringComparison.Ordinal);
        Assert.Contains("household-default", revoked, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, rotated + revoked, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_non_member_cannot_use_the_new_endpoints()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        await host.InitHouseholdAsync("subject-a");
        (await host.LoginAsync("subject-outsider")).Dispose();

        using var zones = await host.SendAsync(HttpMethod.Get, Household + "/time-zones");
        using var zone = await host.SendAsync(HttpMethod.Put, TimeZone, new { timeZoneId = "Europe/Copenhagen" }, ifMatch: "\"2\"");
        using var rotate = await host.SendAsync(HttpMethod.Post, Feed, ifMatch: "\"2\"");
        using var revoke = await host.SendAsync(HttpMethod.Delete, Feed, ifMatch: "\"2\"");

        foreach (var response in new[] { zones, zone, rotate, revoke })
        {
            await ApiTestHost.AssertProblemAsync(response, HttpStatusCode.Forbidden, "not-a-household-member");
        }

        Assert.Equal(2L, await host.Db.ScalarAsMigratorAsync<long>("SELECT count(*) FROM subscribed.mt_events"));
    }
}
