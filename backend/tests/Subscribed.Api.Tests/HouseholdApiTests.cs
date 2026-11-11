using System.Net;
using Subscribed.Platform.Identity;
using Subscribed.TestSupport;
using Xunit;

namespace Subscribed.Api.Tests;

public sealed class HouseholdApiTests(PostgresFixture pg)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AC8_anonymous_401_nonmember_403_member_200_revoked_403_on_next_request()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        await host.InitHouseholdAsync("subject-owner");

        using (var anonymous = await host.SendAsync(HttpMethod.Get, "/api/household"))
        {
            await ApiTestHost.AssertProblemAsync(anonymous, HttpStatusCode.Unauthorized, "unauthenticated");
            Assert.Null(anonymous.Headers.Location);
        }

        (await host.LoginAsync("subject-member")).Dispose();
        using (var nonMember = await host.SendAsync(HttpMethod.Get, "/api/household"))
        {
            await ApiTestHost.AssertProblemAsync(nonMember, HttpStatusCode.Forbidden, "not-a-household-member");
        }

        var (grantExit, _, grantErr) = await host.AdminAsync("household", "grant", "--subject", "subject-member", "--label", "Member B");
        Assert.True(grantExit == 0, grantErr);

        using (var member = await host.SendAsync(HttpMethod.Get, "/api/household"))
        {
            Assert.Equal(HttpStatusCode.OK, member.StatusCode);
            Assert.Equal("\"3\"", member.Headers.ETag?.ToString());
            var body = await ApiTestHost.JsonAsync(member);
            Assert.Equal("household-default", body.GetProperty("id").GetString());
            Assert.Equal("Synthetic Household", body.GetProperty("name").GetString());
            Assert.Equal(3, body.GetProperty("version").GetInt64());
            Assert.Equal(2, body.GetProperty("members").GetArrayLength());
            var raw = body.GetRawText();
            Assert.DoesNotContain("subject", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("issuer", raw, StringComparison.Ordinal);
        }

        var memberId = MemberIdentity.MemberId(ApiTestHost.Issuer, "subject-member");
        var (revokeExit, _, revokeErr) = await host.AdminAsync("household", "revoke", "--member-id", memberId);
        Assert.True(revokeExit == 0, revokeErr);

        using var revoked = await host.SendAsync(HttpMethod.Get, "/api/household");
        await ApiTestHost.AssertProblemAsync(revoked, HttpStatusCode.Forbidden, "not-a-household-member");
        using var session = await host.SendAsync(HttpMethod.Get, "/bff/session");
        Assert.Equal("not-member", (await ApiTestHost.JsonAsync(session)).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Uninitialized_household_denies_every_authenticated_user()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        (await host.LoginAsync("subject-a")).Dispose();
        using var response = await host.SendAsync(HttpMethod.Get, "/api/household");
        await ApiTestHost.AssertProblemAsync(response, HttpStatusCode.Forbidden, "not-a-household-member");
    }

    [Fact]
    public async Task AC9_rename_if_match_rules_validation_and_concurrency()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        await host.InitHouseholdAsync("subject-a");
        (await host.LoginAsync("subject-a")).Dispose();

        using (var missing = await host.SendAsync(HttpMethod.Put, "/api/household/name", new { name = "X" }))
        {
            await ApiTestHost.AssertProblemAsync(missing, HttpStatusCode.PreconditionRequired, "if-match-required");
        }

        using (var invalid = await host.SendAsync(HttpMethod.Put, "/api/household/name", new { name = "   " }, ifMatch: "\"2\""))
        {
            await ApiTestHost.AssertProblemAsync(invalid, HttpStatusCode.BadRequest, "validation-failed");
            var body = await ApiTestHost.JsonAsync(invalid);
            Assert.Equal("Name must be 1–80 characters.", body.GetProperty("errors").GetProperty("name")[0].GetString());
        }

        using (var tooLong = await host.SendAsync(HttpMethod.Put, "/api/household/name", new { name = new string('n', 81) }, ifMatch: "\"2\""))
        {
            await ApiTestHost.AssertProblemAsync(tooLong, HttpStatusCode.BadRequest, "validation-failed");
        }

        using (var valid = await host.SendAsync(HttpMethod.Put, "/api/household/name", new { name = "  Renamed Synthetic Household " }, ifMatch: "\"2\""))
        {
            Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
            Assert.Equal("\"3\"", valid.Headers.ETag?.ToString());
            var body = await ApiTestHost.JsonAsync(valid);
            Assert.Equal(3, body.GetProperty("version").GetInt64());
            Assert.Equal("Renamed Synthetic Household", body.GetProperty("name").GetString());
        }

        using var stale = await host.SendAsync(HttpMethod.Put, "/api/household/name", new { name = "Stale" }, ifMatch: "\"2\"");
        await ApiTestHost.AssertProblemAsync(stale, HttpStatusCode.PreconditionFailed, "concurrency-conflict");
        Assert.Equal(3, (await ApiTestHost.JsonAsync(stale)).GetProperty("currentVersion").GetInt64());
    }

    [Fact]
    public async Task Payload_over_16_kib_is_413_and_malformed_json_is_400()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        await host.InitHouseholdAsync("subject-a");
        (await host.LoginAsync("subject-a")).Dispose();

        using (var big = await host.SendAsync(HttpMethod.Put, "/api/household/name", rawBody: "{\"name\":\"" + new string('x', 17000) + "\"}", ifMatch: "\"2\""))
        {
            await ApiTestHost.AssertProblemAsync(big, HttpStatusCode.RequestEntityTooLarge, "payload-too-large");
        }

        using var malformed = await host.SendAsync(HttpMethod.Put, "/api/household/name", rawBody: "{not json", ifMatch: "\"2\"");
        await ApiTestHost.AssertProblemAsync(malformed, HttpStatusCode.BadRequest, "validation-failed");
    }

    [Fact]
    public async Task AC10_csrf_header_and_origin_rules()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        await host.InitHouseholdAsync("subject-a");
        (await host.LoginAsync("subject-a")).Dispose();

        foreach (var (method, path) in new[] { (HttpMethod.Get, "/api/household"), (HttpMethod.Get, "/bff/session"), (HttpMethod.Post, "/bff/logout"), (HttpMethod.Put, "/api/household/name") })
        {
            using var response = await host.SendAsync(method, path, csrf: false);
            await ApiTestHost.AssertProblemAsync(response, HttpStatusCode.Forbidden, "csrf-header-required");
        }

        using (var put = await host.SendAsync(HttpMethod.Put, "/api/household/name", new { name = "X" }, ifMatch: "\"2\"", origin: "https://evil.example"))
        {
            await ApiTestHost.AssertProblemAsync(put, HttpStatusCode.Forbidden, "origin-mismatch");
        }

        using (var logout = await host.SendAsync(HttpMethod.Post, "/bff/logout", origin: "https://evil.example"))
        {
            await ApiTestHost.AssertProblemAsync(logout, HttpStatusCode.Forbidden, "origin-mismatch");
        }

        // The session survived the rejected logout; same-origin PUT with Origin works.
        using var sameOrigin = await host.SendAsync(HttpMethod.Put, "/api/household/name", new { name = "Same Origin" }, ifMatch: "\"2\"", origin: ApiTestHost.PublicOrigin);
        Assert.Equal(HttpStatusCode.OK, sameOrigin.StatusCode);

        // CORS preflight is never approved (no Access-Control-Allow-* anywhere: asserted on dispose).
        using var preflight = new HttpRequestMessage(HttpMethod.Options, new Uri("/api/household", UriKind.Relative));
        preflight.Headers.Add("Origin", "https://evil.example");
        preflight.Headers.Add("Access-Control-Request-Method", "PUT");
        using var preflightResponse = await host.Client.SendAsync(preflight, Ct);
        Assert.NotEqual(HttpStatusCode.OK, preflightResponse.StatusCode);
    }

    [Fact]
    public async Task Unknown_api_path_is_404_problem_not_spa_and_html_routes_serve_index_with_nonce()
    {
        await using var host = await ApiTestHost.StartAsync(pg);

        using (var unknown = await host.Client.GetAsync(new Uri("/api/x", UriKind.Relative), Ct))
        {
            await ApiTestHost.AssertProblemAsync(unknown, HttpStatusCode.NotFound, "not-found");
        }

        using (var nested = await host.SendAsync(HttpMethod.Post, "/api/does/not/exist"))
        {
            await ApiTestHost.AssertProblemAsync(nested, HttpStatusCode.NotFound, "not-found");
        }

        using var page = await host.Client.GetAsync(new Uri("/household", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", page.Headers.CacheControl?.ToString());
        var html = await page.Content.ReadAsStringAsync(Ct);
        Assert.Contains("<app-root", html, StringComparison.Ordinal);
        Assert.DoesNotContain("__CSP_NONCE__", html, StringComparison.Ordinal);
        var csp = page.Headers.Single("Content-Security-Policy")!;
        var nonce = csp.Split("'nonce-")[1].Split('\'')[0];
        Assert.Contains($"ngCspNonce=\"{nonce}\"", html, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("script-src 'self'", csp, StringComparison.Ordinal);
        Assert.Equal("nosniff", page.Headers.Single("X-Content-Type-Options"));
        Assert.Equal("no-referrer", page.Headers.Single("Referrer-Policy"));
        Assert.StartsWith("max-age=", page.Headers.Single("Strict-Transport-Security"), StringComparison.Ordinal);

        using var second = await host.Client.GetAsync(new Uri("/index.html", UriKind.Relative), Ct);
        Assert.DoesNotContain(nonce, await second.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        using var asset = await host.Client.GetAsync(new Uri("/main-ABCDEFGH.js", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Contains("immutable", asset.Headers.CacheControl?.ToString(), StringComparison.Ordinal);

        using var missingAsset = await host.Client.GetAsync(new Uri("/missing-file.js", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.NotFound, missingAsset.StatusCode);
    }

    [Fact]
    public async Task Database_unavailable_is_503_never_anonymous_or_not_member()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        await host.InitHouseholdAsync("subject-a");
        (await host.LoginAsync("subject-a")).Dispose();

        await pg.AdminAsync($"ALTER DATABASE {host.Db.Name} CONNECTION LIMIT 0");
        try
        {
            using var session = await host.SendAsync(HttpMethod.Get, "/bff/session");
            await ApiTestHost.AssertProblemAsync(session, HttpStatusCode.ServiceUnavailable, "database-unavailable");
            using var api = await host.SendAsync(HttpMethod.Get, "/api/household");
            await ApiTestHost.AssertProblemAsync(api, HttpStatusCode.ServiceUnavailable, "database-unavailable");

            // Local logout still clears the cookie when the database is down.
            using var logout = await host.SendAsync(HttpMethod.Post, "/bff/logout");
            Assert.Equal(HttpStatusCode.OK, logout.StatusCode);
            Assert.Contains(CookieJarHandler.SetCookies(logout), c => c.StartsWith("__Host-subscribed=;", StringComparison.Ordinal));
        }
        finally
        {
            await pg.AdminAsync($"ALTER DATABASE {host.Db.Name} CONNECTION LIMIT -1");
        }
    }
}
