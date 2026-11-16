using System.Net;
using System.Text;
using System.Text.Json;
using Subscribed.Platform.Identity;
using Subscribed.TestSupport;
using Xunit;

namespace Subscribed.Api.Tests;

public sealed class HouseholdApiTests(PostgresFixture pg)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task AssertValidationBodyAsync(HttpResponseMessage response, string field, string message)
    {
        await ApiTestHost.AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation-failed");
        var body = await ApiTestHost.JsonAsync(response);
        Assert.Equal(["errors", "status", "title", "traceId", "type"], body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal("Validation failed", body.GetProperty("title").GetString());
        var errors = body.GetProperty("errors");
        var only = Assert.Single(errors.EnumerateObject());
        Assert.Equal(field, only.Name);
        Assert.Equal(message, Assert.Single(only.Value.EnumerateArray()).GetString());
    }

    private static async Task<HttpResponseMessage> SendNonJsonAsync(ApiTestHost host, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(path, UriKind.Relative));
        request.Headers.Add("X-CSRF", "1");
        request.Headers.TryAddWithoutValidation("If-Match", "\"2\"");
        request.Content = new StringContent("name=X", Encoding.UTF8, "text/plain");
        return await host.Client.SendAsync(request, Ct);
    }

    // Characterization of PUT /api/household/name, added before ReadBodyAsync became generic.
    [Fact]
    public async Task Rename_non_json_content_type_returns_415_problem()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        await host.InitHouseholdAsync("subject-a");
        (await host.LoginAsync("subject-a")).Dispose();

        using var response = await SendNonJsonAsync(host, "/api/household/name");
        await ApiTestHost.AssertProblemAsync(response, HttpStatusCode.UnsupportedMediaType, "unsupported-media-type");
        var body = await ApiTestHost.JsonAsync(response);
        Assert.Equal("The request body must be application/json.", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Rename_malformed_json_returns_400_full_problem_body()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        await host.InitHouseholdAsync("subject-a");
        (await host.LoginAsync("subject-a")).Dispose();

        using var response = await host.SendAsync(HttpMethod.Put, "/api/household/name", rawBody: "{not json", ifMatch: "\"2\"");
        await AssertValidationBodyAsync(response, "name", "Name must be 1–80 characters.");
    }

    [Fact]
    public async Task Rename_81_char_name_returns_400_errors_name_message()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        await host.InitHouseholdAsync("subject-a");
        (await host.LoginAsync("subject-a")).Dispose();

        using var response = await host.SendAsync(HttpMethod.Put, "/api/household/name", new { name = new string('n', 81) }, ifMatch: "\"2\"");
        await AssertValidationBodyAsync(response, "name", "Name must be 1–80 characters.");
    }

    [Fact]
    public async Task Rename_json_null_body_returns_400_errors_name_message()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        await host.InitHouseholdAsync("subject-a");
        (await host.LoginAsync("subject-a")).Dispose();

        using var response = await host.SendAsync(HttpMethod.Put, "/api/household/name", rawBody: "null", ifMatch: "\"2\"");
        await AssertValidationBodyAsync(response, "name", "Name must be 1–80 characters.");
    }

    private static async Task AdminOkAsync(ApiTestHost host, params string[] args)
    {
        var (exit, _, err) = await host.AdminAsync(args);
        Assert.True(exit == 0, err);
    }

    private static string LabelPath(string memberId) => $"/api/household/members/{Uri.EscapeDataString(memberId)}/label";

    private static string MemberIdOf(string subject) => MemberIdentity.MemberId(ApiTestHost.Issuer, subject);

    private static async Task<string> LabelOfAsync(ApiTestHost host, string memberId)
    {
        using var get = await host.SendAsync(HttpMethod.Get, "/api/household");
        var body = await ApiTestHost.JsonAsync(get);
        return body.GetProperty("members").EnumerateArray().Single(m => m.GetProperty("memberId").GetString() == memberId).GetProperty("displayLabel").GetString()!;
    }

    [Fact]
    public async Task Relabel_returns_200_new_etag_and_get_shows_the_new_label()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        await host.InitHouseholdAsync("subject-a", "Owner");
        await AdminOkAsync(host, "household", "grant", "--subject", "subject-b", "--label", "Member B");
        (await host.LoginAsync("subject-a")).Dispose();
        var b = MemberIdOf("subject-b");

        using var response = await host.SendAsync(HttpMethod.Put, LabelPath(b), new { displayLabel = "  Better B  " }, ifMatch: "\"3\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"4\"", response.Headers.ETag?.ToString());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await ApiTestHost.JsonAsync(response);
        Assert.Equal(4, body.GetProperty("version").GetInt64());
        Assert.Equal("Better B", await LabelOfAsync(host, b));
        Assert.Equal("Owner", await LabelOfAsync(host, MemberIdOf("subject-a")));
        using var get = await host.SendAsync(HttpMethod.Get, "/api/household");
        Assert.Equal("\"4\"", get.Headers.ETag?.ToString());
    }

    [Fact]
    public async Task Relabel_if_match_rules_and_concurrency()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        await host.InitHouseholdAsync("subject-a");
        (await host.LoginAsync("subject-a")).Dispose();
        var a = MemberIdOf("subject-a");

        using (var missing = await host.SendAsync(HttpMethod.Put, LabelPath(a), new { displayLabel = "X" }))
        {
            await ApiTestHost.AssertProblemAsync(missing, HttpStatusCode.PreconditionRequired, "if-match-required");
        }

        using (var malformed = await host.SendAsync(HttpMethod.Put, LabelPath(a), new { displayLabel = "X" }, ifMatch: "2"))
        {
            await AssertValidationBodyAsync(malformed, "if-Match", "If-Match must be a quoted version, for example \"3\".");
        }

        using (var ok = await host.SendAsync(HttpMethod.Put, LabelPath(a), new { displayLabel = "Renamed" }, ifMatch: "\"2\""))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        using var stale = await host.SendAsync(HttpMethod.Put, LabelPath(a), new { displayLabel = "Stale" }, ifMatch: "\"2\"");
        await ApiTestHost.AssertProblemAsync(stale, HttpStatusCode.PreconditionFailed, "concurrency-conflict");
        Assert.Equal(3, (await ApiTestHost.JsonAsync(stale)).GetProperty("currentVersion").GetInt64());
        Assert.Equal("Renamed", await LabelOfAsync(host, a));
    }

    [Fact]
    public async Task Relabel_to_the_current_label_returns_200_with_the_current_etag_and_no_version_bump()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        await host.InitHouseholdAsync("subject-a", "Owner");
        (await host.LoginAsync("subject-a")).Dispose();

        using var response = await host.SendAsync(HttpMethod.Put, LabelPath(MemberIdOf("subject-a")), new { displayLabel = " Owner " }, ifMatch: "\"2\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("\"2\"", response.Headers.ETag?.ToString());
        Assert.Equal(2, (await ApiTestHost.JsonAsync(response)).GetProperty("version").GetInt64());
    }

    [Theory]
    [InlineData("{\"displayLabel\":\"\"}")]
    [InlineData("{\"displayLabel\":\"   \"}")]
    [InlineData("{}")]
    [InlineData("{\"displayLabel\":null}")]
    [InlineData("{\"displayLabel\":5}")]
    [InlineData("{\"displayLabel\":{\"a\":1}}")]
    [InlineData("{\"displayLabel\":\"bad\\u0007label\"}")]
    [InlineData("{not json")]
    [InlineData("null")]
    [InlineData("{\"displayLabel\":\"llllllllllllllllllllllllllllllllllllllllllllllllllllllllllllllllll\"}")]
    public async Task Relabel_invalid_bodies_return_400_errors_display_label(string rawBody)
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        await host.InitHouseholdAsync("subject-a");
        (await host.LoginAsync("subject-a")).Dispose();

        using var response = await host.SendAsync(HttpMethod.Put, LabelPath(MemberIdOf("subject-a")), rawBody: rawBody, ifMatch: "\"2\"");

        await AssertValidationBodyAsync(response, "displayLabel", "Label must be 1–64 characters.");
    }

    [Fact]
    public async Task Relabel_payload_over_16_kib_is_413_and_non_json_is_415()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        await host.InitHouseholdAsync("subject-a");
        (await host.LoginAsync("subject-a")).Dispose();
        var path = LabelPath(MemberIdOf("subject-a"));

        using (var big = await host.SendAsync(HttpMethod.Put, path, rawBody: "{\"displayLabel\":\"" + new string('x', 17000) + "\"}", ifMatch: "\"2\""))
        {
            await ApiTestHost.AssertProblemAsync(big, HttpStatusCode.RequestEntityTooLarge, "payload-too-large");
        }

        using var plain = await SendNonJsonAsync(host, path);
        await ApiTestHost.AssertProblemAsync(plain, HttpStatusCode.UnsupportedMediaType, "unsupported-media-type");
    }

    [Fact]
    public async Task Relabel_unknown_member_is_404_with_a_current_if_match_and_412_with_a_stale_one()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        await host.InitHouseholdAsync("subject-a");
        (await host.LoginAsync("subject-a")).Dispose();

        using (var unknown = await host.SendAsync(HttpMethod.Put, LabelPath("0000000000000000"), new { displayLabel = "Ghost" }, ifMatch: "\"2\""))
        {
            await ApiTestHost.AssertProblemAsync(unknown, HttpStatusCode.NotFound, "not-found");
            Assert.Equal("Member not found.", (await ApiTestHost.JsonAsync(unknown)).GetProperty("title").GetString());
        }

        using (var invalidWins = await host.SendAsync(HttpMethod.Put, LabelPath("0000000000000000"), new { displayLabel = "" }, ifMatch: "\"2\""))
        {
            await AssertValidationBodyAsync(invalidWins, "displayLabel", "Label must be 1–64 characters.");
        }

        using var stale = await host.SendAsync(HttpMethod.Put, LabelPath("0000000000000000"), new { displayLabel = "Ghost" }, ifMatch: "\"1\"");
        await ApiTestHost.AssertProblemAsync(stale, HttpStatusCode.PreconditionFailed, "concurrency-conflict");
    }

    [Fact]
    public async Task Relabel_of_a_revoked_member_is_404()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        await host.InitHouseholdAsync("subject-a");
        await AdminOkAsync(host, "household", "grant", "--subject", "subject-b", "--label", "Member B");
        var b = MemberIdOf("subject-b");
        await AdminOkAsync(host, "household", "revoke", "--member-id", b);
        (await host.LoginAsync("subject-a")).Dispose();

        using var response = await host.SendAsync(HttpMethod.Put, LabelPath(b), new { displayLabel = "Ghost" }, ifMatch: "\"4\"");

        await ApiTestHost.AssertProblemAsync(response, HttpStatusCode.NotFound, "not-found");
    }

    [Fact]
    public async Task Relabel_resolves_a_url_encoded_member_id_and_rejects_reserved_characters_as_404()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        await host.InitHouseholdAsync("subject-a");
        (await host.LoginAsync("subject-a")).Dispose();

        using var encoded = await host.SendAsync(HttpMethod.Put, "/api/household/members/" + string.Concat(MemberIdOf("subject-a").Select(c => "%" + ((int)c).ToString("X2", System.Globalization.CultureInfo.InvariantCulture))) + "/label", new { displayLabel = "Encoded" }, ifMatch: "\"2\"");
        Assert.Equal(HttpStatusCode.OK, encoded.StatusCode);

        using var reserved = await host.SendAsync(HttpMethod.Put, "/api/household/members/a%2Fb/label", new { displayLabel = "X" }, ifMatch: "\"3\"");
        await ApiTestHost.AssertProblemAsync(reserved, HttpStatusCode.NotFound, "not-found");
    }

    [Fact]
    public async Task Relabel_by_a_non_member_is_403()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        await host.InitHouseholdAsync("subject-a");
        (await host.LoginAsync("subject-outsider")).Dispose();

        using var response = await host.SendAsync(HttpMethod.Put, LabelPath(MemberIdOf("subject-a")), new { displayLabel = "X" }, ifMatch: "\"2\"");

        await ApiTestHost.AssertProblemAsync(response, HttpStatusCode.Forbidden, "not-a-household-member");
    }

    [Fact]
    public async Task Relabel_requires_authentication_csrf_and_origin()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        await host.InitHouseholdAsync("subject-a");
        var path = LabelPath(MemberIdOf("subject-a"));

        using (var anonymous = await host.SendAsync(HttpMethod.Put, path, new { displayLabel = "X" }, ifMatch: "\"2\""))
        {
            await ApiTestHost.AssertProblemAsync(anonymous, HttpStatusCode.Unauthorized, "unauthenticated");
        }

        (await host.LoginAsync("subject-a")).Dispose();
        using (var noCsrf = await host.SendAsync(HttpMethod.Put, path, new { displayLabel = "X" }, ifMatch: "\"2\"", csrf: false))
        {
            await ApiTestHost.AssertProblemAsync(noCsrf, HttpStatusCode.Forbidden, "csrf-header-required");
        }

        using (var evil = await host.SendAsync(HttpMethod.Put, path, new { displayLabel = "X" }, ifMatch: "\"2\"", origin: "https://evil.example"))
        {
            await ApiTestHost.AssertProblemAsync(evil, HttpStatusCode.Forbidden, "origin-mismatch");
        }

        using var sameOrigin = await host.SendAsync(HttpMethod.Put, path, new { displayLabel = "Same Origin" }, ifMatch: "\"2\"", origin: ApiTestHost.PublicOrigin);
        Assert.Equal(HttpStatusCode.OK, sameOrigin.StatusCode);
    }

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
