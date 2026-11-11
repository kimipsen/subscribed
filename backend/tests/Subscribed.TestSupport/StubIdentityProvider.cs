using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Subscribed.TestSupport;

/// <summary>
/// In-process OpenID Provider (TEST ASSEMBLY ONLY): discovery, JWKS, token endpoint with PKCE S256
/// and client-secret checks, RS256 id_tokens with a per-run key. The app reaches it only through
/// the OIDC handler's backchannel, which the tests replace; production code has no bypass.
/// </summary>
public sealed class StubIdentityProvider : IAsyncDisposable
{
    public const string Authority = "https://idp.test/realms/stub";
    public const string ClientId = "subscribed-test";

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RsaSecurityKey _key;
    private readonly ConcurrentDictionary<string, PendingCode> _codes = new(StringComparer.Ordinal);
    private readonly ConcurrentBag<string> _secrets = [];
    private WebApplication? _app;

    public StubIdentityProvider(string clientSecret)
    {
        ClientSecret = clientSecret;
        _key = new RsaSecurityKey(_rsa) { KeyId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)) };
    }

    public string ClientSecret { get; }

    /// <summary>When false every endpoint answers 503 (simulates a stopped IdP).</summary>
    public bool Available { get; set; } = true;

    /// <summary>Override the nonce placed into the next id_tokens (wrong-nonce tests).</summary>
    public string? NonceOverride { get; set; }

    /// <summary>Every code, token, state and nonce this IdP saw or issued (forbidden in logs).</summary>
    public IReadOnlyCollection<string> SeenSecrets => _secrets.ToArray();

    public HttpMessageHandler Handler => _app!.GetTestServer().CreateHandler();

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Use(async (ctx, next) =>
        {
            if (!Available)
            {
                ctx.Response.StatusCode = 503;
                return;
            }

            await next(ctx);
        });
        _app.MapGet("/realms/stub/.well-known/openid-configuration", () => Results.Json(new Dictionary<string, object>
        {
            ["issuer"] = Authority,
            ["authorization_endpoint"] = $"{Authority}/protocol/openid-connect/auth",
            ["token_endpoint"] = $"{Authority}/protocol/openid-connect/token",
            ["jwks_uri"] = $"{Authority}/protocol/openid-connect/certs",
            ["end_session_endpoint"] = $"{Authority}/protocol/openid-connect/logout",
            ["response_types_supported"] = new[] { "code" },
            ["subject_types_supported"] = new[] { "public" },
            ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
            ["code_challenge_methods_supported"] = new[] { "S256" },
        }));
        _app.MapGet("/realms/stub/protocol/openid-connect/certs", () =>
        {
            var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(_rsa.ExportParameters(false)) { KeyId = _key.KeyId });
            return Results.Json(new { keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid = jwk.Kid, n = jwk.N, e = jwk.E } } });
        });
        _app.MapPost("/realms/stub/protocol/openid-connect/token", (Delegate)TokenAsync);
        await _app.StartAsync();
    }

    /// <summary>
    /// Plays the browser at the authorization endpoint: validates the request the app built and
    /// returns the callback URL (code, state, iss) the IdP would redirect to.
    /// </summary>
    public Uri Authorize(Uri authorizationRequest, string subject, string? name, string? preferredUsername = null, bool deny = false)
    {
        ArgumentNullException.ThrowIfNull(authorizationRequest);
        var query = QueryHelpers.ParseQuery(authorizationRequest.Query);
        string Q(string key) => query.TryGetValue(key, out var v) ? v.ToString() : "";
        if (Q("client_id") != ClientId || Q("response_type") != "code" || Q("code_challenge_method") != "S256" || string.IsNullOrEmpty(Q("code_challenge")))
        {
            throw new InvalidOperationException("Authorization request does not meet the code + PKCE S256 contract.");
        }

        var state = Q("state");
        _secrets.Add(state);
        _secrets.Add(Q("nonce"));
        var redirect = Q("redirect_uri");
        if (deny)
        {
            return new Uri(QueryHelpers.AddQueryString(redirect, new Dictionary<string, string?> { ["error"] = "access_denied", ["state"] = state, ["iss"] = Authority }));
        }

        var code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        _secrets.Add(code);
        _codes[code] = new PendingCode(Q("code_challenge"), redirect, Q("nonce"), subject, name, preferredUsername);
        return new Uri(QueryHelpers.AddQueryString(redirect, new Dictionary<string, string?> { ["code"] = code, ["state"] = state, ["iss"] = Authority }));
    }

    private async Task<IResult> TokenAsync(HttpContext ctx)
    {
        var form = await ctx.Request.ReadFormAsync();
        string F(string key) => form.TryGetValue(key, out var v) ? v.ToString() : "";
        var (clientId, secret) = (F("client_id"), F("client_secret"));
        var basic = ctx.Request.Headers.Authorization.ToString();
        if (basic.StartsWith("Basic ", StringComparison.Ordinal))
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(basic[6..])).Split(':', 2);
            (clientId, secret) = (Uri.UnescapeDataString(parts[0]), Uri.UnescapeDataString(parts[1]));
        }

        if (clientId != ClientId || secret != ClientSecret)
        {
            return Results.Json(new { error = "invalid_client" }, statusCode: 401);
        }

        if (F("grant_type") != "authorization_code" || !_codes.TryRemove(F("code"), out var pending) || pending.RedirectUri != F("redirect_uri"))
        {
            return Results.Json(new { error = "invalid_grant" }, statusCode: 400);
        }

        var verifierHash = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(F("code_verifier"))));
        if (verifierHash != pending.Challenge)
        {
            return Results.Json(new { error = "invalid_grant", error_description = "pkce" }, statusCode: 400);
        }

        var claims = new List<Claim> { new("sub", pending.Subject), new("nonce", NonceOverride ?? pending.Nonce) };
        if (pending.Name is not null)
        {
            claims.Add(new Claim("name", pending.Name));
        }

        if (pending.PreferredUsername is not null)
        {
            claims.Add(new Claim("preferred_username", pending.PreferredUsername));
        }

        var now = DateTime.UtcNow;
        var idToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Authority,
            Audience = ClientId,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now,
            NotBefore = now.AddSeconds(-5),
            Expires = now.AddMinutes(5),
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256),
        });
        var accessToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        var refreshToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        _secrets.Add(idToken);
        _secrets.Add(accessToken);
        _secrets.Add(refreshToken);
        return Results.Json(new Dictionary<string, object>
        {
            ["access_token"] = accessToken,
            ["refresh_token"] = refreshToken,
            ["id_token"] = idToken,
            ["token_type"] = "Bearer",
            ["expires_in"] = 300,
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        _rsa.Dispose();
    }

    private sealed record PendingCode(string Challenge, string RedirectUri, string Nonce, string Subject, string? Name, string? PreferredUsername);

    /// <summary>Unused helper kept internal to make JSON parsing of discovery explicit in tests.</summary>
    internal static JsonDocument Parse(string json) => JsonDocument.Parse(json);
}
