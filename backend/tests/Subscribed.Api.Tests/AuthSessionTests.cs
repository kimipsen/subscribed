using System.Globalization;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Subscribed.Api.Bff;
using Subscribed.Platform.Identity;
using Subscribed.TestSupport;
using Xunit;

[assembly: AssemblyFixture(typeof(PostgresFixture))]

namespace Subscribed.Api.Tests;

public sealed class AuthSessionTests(PostgresFixture pg)
{
    private const string SessionCookie = "__Host-subscribed";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AC11_login_redirects_with_code_pkce_query_mode_and_configured_redirect_uri_despite_spoofed_host()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/bff/login?returnUrl=/household", UriKind.Relative));
        request.Headers.Host = "evil.example";
        using var response = await host.Client.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.StartsWith(StubIdentityProvider.Authority + "/protocol/openid-connect/auth", location.ToString(), StringComparison.Ordinal);
        var q = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("code", q["response_type"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.False(string.IsNullOrEmpty(q["code_challenge"]));
        Assert.Equal("query", q["response_mode"]);
        Assert.Equal("https://app.test/signin-oidc", q["redirect_uri"]);
        Assert.Equal("openid profile", q["scope"]);
        Assert.Equal(StubIdentityProvider.ClientId, q["client_id"]);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData("//x")]
    [InlineData("/\\x")]
    [InlineData("https://x")]
    [InlineData("x")]
    [InlineData("/a\rb")]
    public async Task AC11_invalid_return_urls_are_rejected(string returnUrl)
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        using var response = await host.Client.GetAsync(new Uri("/bff/login?returnUrl=" + Uri.EscapeDataString(returnUrl), UriKind.Relative), Ct);
        await ApiTestHost.AssertProblemAsync(response, HttpStatusCode.BadRequest, "invalid-return-url");
    }

    [Fact]
    public async Task AC11_overlong_return_url_is_rejected_and_signed_in_login_redirects_locally()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        using var tooLong = await host.Client.GetAsync(new Uri("/bff/login?returnUrl=/" + new string('a', 512), UriKind.Relative), Ct);
        await ApiTestHost.AssertProblemAsync(tooLong, HttpStatusCode.BadRequest, "invalid-return-url");

        using var callback = await host.LoginAsync("subject-a");
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/household", callback.Headers.Location!.OriginalString);

        using var again = await host.Client.GetAsync(new Uri("/bff/login?returnUrl=/somewhere", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.Redirect, again.StatusCode);
        Assert.Equal("/somewhere", again.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Session_states_anonymous_not_member_member()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        await host.InitHouseholdAsync("subject-member", "Member A");

        using (var anon = await host.SendAsync(HttpMethod.Get, "/bff/session"))
        {
            Assert.Equal(HttpStatusCode.OK, anon.StatusCode);
            Assert.Equal("""{"state":"anonymous"}""", await anon.Content.ReadAsStringAsync(Ct));
            Assert.Equal("no-store", anon.Headers.CacheControl?.ToString());
        }

        (await host.LoginAsync("subject-outsider", "Synthetic User B")).Dispose();
        using (var notMember = await host.SendAsync(HttpMethod.Get, "/bff/session"))
        {
            var body = await ApiTestHost.JsonAsync(notMember);
            Assert.Equal("not-member", body.GetProperty("state").GetString());
            Assert.Equal("Synthetic User B", body.GetProperty("user").GetProperty("displayName").GetString());
            Assert.Equal("subject-outsider", body.GetProperty("user").GetProperty("subject").GetString());
            Assert.False(body.GetProperty("user").TryGetProperty("memberId", out _));
            Assert.True(body.TryGetProperty("expiresAt", out _));
        }

        host.Jar.Remove(SessionCookie);
        (await host.LoginAsync("subject-member", "Synthetic User A")).Dispose();
        using var member = await host.SendAsync(HttpMethod.Get, "/bff/session");
        var m = await ApiTestHost.JsonAsync(member);
        Assert.Equal("member", m.GetProperty("state").GetString());
        Assert.Equal(MemberIdentity.MemberId(ApiTestHost.Issuer, "subject-member"), m.GetProperty("user").GetProperty("memberId").GetString());
        Assert.False(m.GetProperty("user").TryGetProperty("subject", out _));
        Assert.Contains(host.Logs.Entries, e => e.Contains("security login " + MemberIdentity.MemberId(ApiTestHost.Issuer, "subject-member"), StringComparison.Ordinal));
        Assert.Contains(host.Logs.Entries, e => e.Contains("security login non-member", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AC12_worst_case_claims_give_one_bounded_cookie_without_tokens()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        var sub = new string('s', 255);
        using var callback = await host.LoginAsync(sub, new string('N', 4990) + "😀\u0001xxxxxxxx");
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);

        var sessionCookies = CookieJarHandler.SetCookies(callback).Where(c => c.StartsWith(SessionCookie, StringComparison.Ordinal)).ToList();
        Assert.Single(sessionCookies);
        Assert.StartsWith(SessionCookie + "=", sessionCookies[0], StringComparison.Ordinal);
        Assert.Contains("httponly", sessionCookies[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", sessionCookies[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", sessionCookies[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", sessionCookies[0], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires=", sessionCookies[0], StringComparison.OrdinalIgnoreCase);
        var value = host.Jar.Get(SessionCookie)!;
        Assert.True(value.Length <= 4050, $"cookie length {value.Length}");
        Assert.DoesNotContain(host.Jar.Names, n => n.StartsWith(SessionCookie + "C", StringComparison.Ordinal));

        var options = host.App.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var ticket = options.TicketDataFormat.Unprotect(value)!;
        Assert.Equal(["iss", "issued_at", "name", "sub"], ticket.Principal.Claims.Select(c => c.Type).Order());
        Assert.Equal(64, ticket.Principal.FindFirst("name")!.Value.Length);
        Assert.Equal(sub, ticket.Principal.FindFirst("sub")!.Value);
        Assert.DoesNotContain(ticket.Properties.Items.Keys, k => k.Contains("Token", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(ticket.Properties.Items.Values, v => v is not null && host.Stub.SeenSecrets.Contains(v));
    }

    [Fact]
    public async Task AC12_subject_over_255_chars_fails_the_callback()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        using var callback = await host.LoginAsync(new string('s', 256));
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/welcome?authError=callback-failed", callback.Headers.Location!.OriginalString);
        Assert.False(host.Jar.Has(SessionCookie));
    }

    [Fact]
    public async Task AC12_logout_signed_in_clears_cookie_and_returns_client_id_logout_without_hint()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        (await host.LoginAsync("subject-a")).Dispose();
        using var response = await host.SendAsync(HttpMethod.Post, "/bff/logout", origin: ApiTestHost.PublicOrigin);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(CookieJarHandler.SetCookies(response), c => c.StartsWith(SessionCookie + "=;", StringComparison.Ordinal) && c.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));
        var body = await ApiTestHost.JsonAsync(response);
        Assert.True(body.GetProperty("idpLogout").GetBoolean());
        var url = new Uri(body.GetProperty("redirectUrl").GetString()!);
        Assert.StartsWith(StubIdentityProvider.Authority + "/protocol/openid-connect/logout", url.ToString(), StringComparison.Ordinal);
        var q = QueryHelpers.ParseQuery(url.Query);
        Assert.Equal(StubIdentityProvider.ClientId, q["client_id"]);
        Assert.Equal("https://app.test/signed-out", q["post_logout_redirect_uri"]);
        Assert.False(q.ContainsKey("id_token_hint"));
        Assert.False(host.Jar.Has(SessionCookie));

        using var session = await host.SendAsync(HttpMethod.Get, "/bff/session");
        Assert.Equal("anonymous", (await ApiTestHost.JsonAsync(session)).GetProperty("state").GetString());
    }

    [Fact]
    public async Task Logout_discovery_failure_clears_the_local_cookie_and_logs_the_fallback()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        (await host.LoginAsync("subject-a")).Dispose();
        host.App.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>()
            .Get(OidcSetup.Scheme).ConfigurationManager = new UnavailableOidcConfiguration();

        using var response = await host.SendAsync(HttpMethod.Post, "/bff/logout", origin: ApiTestHost.PublicOrigin);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"redirectUrl":"/signed-out?idp=0","idpLogout":false}""", await response.Content.ReadAsStringAsync(Ct));
        Assert.False(host.Jar.Has(SessionCookie));
        Assert.Contains(host.Logs.Entries, e => e.Contains(SecurityEvents.IdpDiscoveryUnavailable, StringComparison.Ordinal));
        using var session = await host.SendAsync(HttpMethod.Get, "/bff/session");
        Assert.Equal("anonymous", (await ApiTestHost.JsonAsync(session)).GetProperty("state").GetString());
    }

    private sealed class UnavailableOidcConfiguration : IConfigurationManager<OpenIdConnectConfiguration>
    {
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) =>
            Task.FromException<OpenIdConnectConfiguration>(new IOException("Synthetic discovery failure."));

        public void RequestRefresh()
        {
        }
    }

    [Fact]
    public async Task AC12_logout_anonymous_or_idp_logout_none_is_local_only()
    {
        await using (var host = await ApiTestHost.StartAsync(pg))
        {
            using var anonymous = await host.SendAsync(HttpMethod.Post, "/bff/logout");
            Assert.Equal("""{"redirectUrl":"/signed-out?idp=0","idpLogout":false}""", await anonymous.Content.ReadAsStringAsync(Ct));
            Assert.Contains(CookieJarHandler.SetCookies(anonymous), c => c.StartsWith(SessionCookie + "=;", StringComparison.Ordinal));
        }

        await using var none = await ApiTestHost.StartAsync(pg, c => c["Oidc:IdpLogout"] = "None");
        (await none.LoginAsync("subject-a")).Dispose();
        using var response = await none.SendAsync(HttpMethod.Post, "/bff/logout");
        Assert.Equal("""{"redirectUrl":"/signed-out?idp=0","idpLogout":false}""", await response.Content.ReadAsStringAsync(Ct));
        Assert.False(none.Jar.Has(SessionCookie));
    }

    [Fact]
    public async Task AC12b_idle_timeout_slides_and_expires_after_two_hours()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        await host.InitHouseholdAsync("subject-a");
        (await host.LoginAsync("subject-a")).Dispose();
        var issuedAt = host.Time.GetUtcNow();

        host.Time.Advance(TimeSpan.FromMinutes(119));
        using (var ok = await host.SendAsync(HttpMethod.Get, "/api/household"))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            Assert.Contains(CookieJarHandler.SetCookies(ok), c => c.StartsWith(SessionCookie + "=", StringComparison.Ordinal) && !c.StartsWith(SessionCookie + "=;", StringComparison.Ordinal));
        }

        using (var session = await host.SendAsync(HttpMethod.Get, "/bff/session"))
        {
            var expires = (await ApiTestHost.JsonAsync(session)).GetProperty("expiresAt").GetDateTimeOffset();
            Assert.True(expires <= issuedAt + TimeSpan.FromHours(12));
            Assert.True(expires > host.Time.GetUtcNow() + TimeSpan.FromMinutes(110));
        }

        var stale = host.Jar.Get(SessionCookie)!;
        host.Time.Advance(TimeSpan.FromMinutes(121));
        using (var expired = await host.SendAsync(HttpMethod.Get, "/api/household"))
        {
            await ApiTestHost.AssertProblemAsync(expired, HttpStatusCode.Unauthorized, "unauthenticated");
            Assert.Null(expired.Headers.Location);
            Assert.Contains(CookieJarHandler.SetCookies(expired), c => c.StartsWith(SessionCookie + "=;", StringComparison.Ordinal));
        }

        host.Jar.Set(SessionCookie, stale);
        using var anon = await host.SendAsync(HttpMethod.Get, "/bff/session");
        Assert.Equal("anonymous", (await ApiTestHost.JsonAsync(anon)).GetProperty("state").GetString());
        Assert.Contains(host.Logs.Entries, e => e.Contains("session-expired reason=idle", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AC12b_absolute_limit_of_twelve_hours_despite_activity()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        await host.InitHouseholdAsync("subject-a");
        (await host.LoginAsync("subject-a")).Dispose();
        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(host.Time.GetUtcNow().ToUnixTimeSeconds());

        for (var hour = 1; hour <= 11; hour++)
        {
            host.Time.Advance(TimeSpan.FromHours(1));
            using var ok = await host.SendAsync(HttpMethod.Get, "/api/household");
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        host.Time.Advance(TimeSpan.FromMinutes(59)); // 11 h 59 min
        using (var ok = await host.SendAsync(HttpMethod.Get, "/bff/session"))
        {
            var body = await ApiTestHost.JsonAsync(ok);
            Assert.Equal("member", body.GetProperty("state").GetString());
            Assert.True(body.GetProperty("expiresAt").GetDateTimeOffset() <= issuedAt + TimeSpan.FromHours(12));
        }

        var stale = host.Jar.Get(SessionCookie)!;
        host.Time.Advance(TimeSpan.FromMinutes(2)); // 12 h 01 min
        using (var expired = await host.SendAsync(HttpMethod.Get, "/api/household"))
        {
            await ApiTestHost.AssertProblemAsync(expired, HttpStatusCode.Unauthorized, "unauthenticated");
            Assert.Contains(CookieJarHandler.SetCookies(expired), c => c.StartsWith(SessionCookie + "=;", StringComparison.Ordinal));
        }

        host.Jar.Set(SessionCookie, stale);
        using var anon = await host.SendAsync(HttpMethod.Get, "/bff/session");
        Assert.Equal("anonymous", (await ApiTestHost.JsonAsync(anon)).GetProperty("state").GetString());
        Assert.Contains(host.Logs.Entries, e => e.Contains("session-expired reason=absolute", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("future")]
    [InlineData("missing")]
    [InlineData("garbage")]
    public async Task AC12b_future_missing_or_unparseable_issued_at_is_treated_as_expired(string kind)
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        var now = host.Time.GetUtcNow();
        var claims = new List<Claim> { new("iss", ApiTestHost.Issuer), new("sub", "subject-a"), new("name", "A") };
        if (kind != "missing")
        {
            claims.Add(new Claim("issued_at", kind == "future" ? (now + TimeSpan.FromMinutes(10)).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture) : "abc"));
        }

        // A validly protected ticket (correct keys) whose content breaks the absolute-limit rules.
        var options = host.App.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var properties = new AuthenticationProperties { IssuedUtc = now, ExpiresUtc = now + TimeSpan.FromHours(2) };
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, "subscribed")), properties, CookieAuthenticationDefaults.AuthenticationScheme);
        host.Jar.Set(SessionCookie, options.TicketDataFormat.Protect(ticket));

        using var session = await host.SendAsync(HttpMethod.Get, "/bff/session");
        Assert.Equal("anonymous", (await ApiTestHost.JsonAsync(session)).GetProperty("state").GetString());
        Assert.Contains(CookieJarHandler.SetCookies(session), c => c.StartsWith(SessionCookie + "=;", StringComparison.Ordinal));
        Assert.Contains(host.Logs.Entries, e => e.Contains("session-expired reason=absolute", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tampered_session_cookie_is_deleted_and_request_continues_anonymously()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        host.Jar.Set(SessionCookie, "CfDJ8-not-a-valid-ticket");
        using var session = await host.SendAsync(HttpMethod.Get, "/bff/session");
        Assert.Equal("anonymous", (await ApiTestHost.JsonAsync(session)).GetProperty("state").GetString());
        Assert.Contains(CookieJarHandler.SetCookies(session), c => c.StartsWith(SessionCookie + "=;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AC21_tampered_state_fails_without_session()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        using var callback = await host.LoginAsync("subject-a", tamperCallback: u => new Uri(u.ToString().Replace("state=", "state=X", StringComparison.Ordinal)));
        Assert.Equal("/welcome?authError=callback-failed", callback.Headers.Location!.OriginalString);
        Assert.False(host.Jar.Has(SessionCookie));
        Assert.Contains(host.Logs.Entries, e => e.Contains("security callback-failed kind=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AC21_missing_correlation_cookie_fails_without_session()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        using var callback = await host.LoginAsync("subject-a", beforeCallback: () =>
        {
            foreach (var name in host.Jar.Names.Where(n => n.StartsWith(".AspNetCore.Correlation", StringComparison.Ordinal)).ToList())
            {
                host.Jar.Remove(name);
            }
        });
        Assert.Equal("/welcome?authError=callback-failed", callback.Headers.Location!.OriginalString);
        Assert.False(host.Jar.Has(SessionCookie));
    }

    [Fact]
    public async Task AC21_wrong_nonce_fails_without_session()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        host.Stub.NonceOverride = "attacker-nonce-value";
        using var callback = await host.LoginAsync("subject-a");
        Assert.Equal("/welcome?authError=callback-failed", callback.Headers.Location!.OriginalString);
        Assert.False(host.Jar.Has(SessionCookie));
    }

    [Fact]
    public async Task AC21_access_denied_is_cancelled_and_callback_needs_no_csrf_header()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        using var denied = await host.LoginAsync("subject-a", deny: true);
        Assert.Equal("/welcome?authError=cancelled", denied.Headers.Location!.OriginalString);
        Assert.False(host.Jar.Has(SessionCookie));

        using var ok = await host.LoginAsync("subject-a"); // LoginAsync never sends X-CSRF
        Assert.Equal("/household", ok.Headers.Location!.OriginalString);
        Assert.True(host.Jar.Has(SessionCookie));
    }

    [Fact]
    public async Task AC15_logs_contain_safe_security_events_and_no_secrets()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        await host.InitHouseholdAsync("subject-a");
        (await host.LoginAsync("subject-a")).Dispose();
        using (var get = await host.SendAsync(HttpMethod.Get, "/api/household"))
        {
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        }

        using (var put = await host.SendAsync(HttpMethod.Put, "/api/household/name", new { name = "Renamed" }, ifMatch: "\"1\""))
        {
            Assert.Equal(HttpStatusCode.PreconditionFailed, put.StatusCode);
        }

        using (var csrf = await host.SendAsync(HttpMethod.Get, "/api/household", csrf: false))
        {
            Assert.Equal(HttpStatusCode.Forbidden, csrf.StatusCode);
        }

        (await host.SendAsync(HttpMethod.Post, "/bff/logout")).Dispose();
        host.Stub.NonceOverride = "bad-nonce-123456";
        (await host.LoginAsync("subject-a")).Dispose();

        var entries = host.Logs.Entries;
        Assert.Contains(entries, e => e.Contains("request GET /api/household", StringComparison.Ordinal) && e.Contains("|StatusCode=200", StringComparison.Ordinal));
        Assert.Contains(entries, e => e.Contains("security concurrency-conflict", StringComparison.Ordinal));
        Assert.Contains(entries, e => e.Contains("security csrf-rejected", StringComparison.Ordinal));
        Assert.Contains(entries, e => e.Contains("security logout signed-in", StringComparison.Ordinal));
        Assert.Contains(entries, e => e.Contains("security callback-failed kind=nonce-invalid", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, e => e.Contains("code=", StringComparison.Ordinal));
        Assert.Empty(host.Logs.Find(host.ForbiddenLogValues()));
    }
}
