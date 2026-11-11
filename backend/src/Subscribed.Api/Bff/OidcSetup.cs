using System.Globalization;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Subscribed.Api.Configuration;
using Subscribed.Household;
using Subscribed.Platform.Identity;

namespace Subscribed.Api.Bff;

/// <summary>Cookie session + confidential OIDC client (authorization code + PKCE, response_mode=query).</summary>
public static class OidcSetup
{
    public const string Scheme = OpenIdConnectDefaults.AuthenticationScheme;
    public const string CallbackPath = "/signin-oidc";
    public const string DefaultDisplayName = "Signed-in user";
    public const int MaxDisplayNameLength = 64;

    public static void AddBffAuthentication(this IServiceCollection services, SubscribedSettings settings)
    {
        var secure = settings.IsHttpOrigin ? CookieSecurePolicy.None : CookieSecurePolicy.Always;
        services.AddAuthentication(o =>
            {
                o.DefaultScheme = SessionCookie.Scheme;
                o.DefaultChallengeScheme = Scheme;
            })
            .AddCookie(SessionCookie.Scheme, o =>
            {
                o.Cookie.Name = settings.SessionCookieName;
                o.Cookie.HttpOnly = true;
                o.Cookie.SecurePolicy = secure;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.Cookie.Path = "/";
                o.ExpireTimeSpan = SessionCookie.Idle;
                o.SlidingExpiration = false; // replaced by the capped renewal in SessionCookie.ValidateAsync
                o.Events.OnValidatePrincipal = SessionCookie.ValidateAsync;
                o.Events.OnRedirectToLogin = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
                o.Events.OnRedirectToAccessDenied = ctx =>
                {
                    ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            })
            .AddOpenIdConnect(Scheme, o =>
            {
                o.Authority = settings.OidcAuthority;
                o.RequireHttpsMetadata = settings.OidcAuthority.StartsWith("https:", StringComparison.Ordinal);
                o.ClientId = settings.ClientId;
                o.ClientSecret = settings.ClientSecret;
                o.ResponseType = OpenIdConnectResponseType.Code;
                o.ResponseMode = OpenIdConnectResponseMode.Query;
                o.UsePkce = true;
                o.SaveTokens = false;
                o.MapInboundClaims = false;
                o.GetClaimsFromUserInfoEndpoint = false;

                // The default claim actions delete "iss" (among others) AFTER OnTokenValidated; the
                // ticket is already reduced to exactly iss/sub/name/issued_at there.
                o.ClaimActions.Clear();
                o.Scope.Clear();
                o.Scope.Add("openid");
                o.Scope.Add("profile");
                o.CallbackPath = CallbackPath;
                o.SignInScheme = SessionCookie.Scheme;
                o.RemoteAuthenticationTimeout = TimeSpan.FromMinutes(15);
                o.TokenValidationParameters.NameClaimType = SessionClaims.Name;
                foreach (var cookie in new[] { o.CorrelationCookie, o.NonceCookie })
                {
                    cookie.Path = CallbackPath;
                    cookie.HttpOnly = true;
                    cookie.SameSite = SameSiteMode.Lax;
                    cookie.SecurePolicy = secure;
                }

                o.ProtocolValidator.NonceLifetime = TimeSpan.FromMinutes(15);
                o.Events = new OpenIdConnectEvents
                {
                    OnRedirectToIdentityProvider = ctx =>
                    {
                        // Never built from Host: the configured trusted origin only.
                        ctx.ProtocolMessage.RedirectUri = settings.Origin + CallbackPath;
                        ctx.ProtocolMessage.ResponseMode = OpenIdConnectResponseMode.Query;
                        return Task.CompletedTask;
                    },
                    OnMessageReceived = ValidateAuthorizationResponseIssuerAsync,
                    OnTokenValidated = ctx => NormalizeTicket(ctx, settings),
                    OnTicketReceived = OnTicketReceivedAsync,
                    OnAccessDenied = ctx =>
                    {
                        SecurityLog.Info(Logger(ctx.HttpContext), SecurityEvents.CallbackFailed, "kind=cancelled");
                        ctx.Response.Redirect("/welcome?authError=cancelled");
                        ctx.HandleResponse();
                        return Task.CompletedTask;
                    },
                    OnRemoteFailure = ctx =>
                    {
                        var cancelled = string.Equals(ctx.Request.Query["error"], "access_denied", StringComparison.Ordinal);
                        var kind = cancelled ? "cancelled" : ClassifyFailure(ctx.Failure);
                        SecurityLog.Info(Logger(ctx.HttpContext), SecurityEvents.CallbackFailed, $"kind={kind}");
                        ctx.Response.Redirect(cancelled ? "/welcome?authError=cancelled" : "/welcome?authError=callback-failed");
                        ctx.HandleResponse();
                        return Task.CompletedTask;
                    },
                };
            });
    }

    /// <summary>RFC 9207: when the authorization response carries iss, it must equal the discovered issuer.</summary>
    private static async Task ValidateAuthorizationResponseIssuerAsync(MessageReceivedContext ctx)
    {
        var iss = ctx.ProtocolMessage?.Iss;
        if (string.IsNullOrEmpty(iss) || ctx.Options.ConfigurationManager is null)
        {
            return;
        }

        var configuration = await ctx.Options.ConfigurationManager.GetConfigurationAsync(ctx.HttpContext.RequestAborted);
        if (!string.Equals(configuration.Issuer, iss, StringComparison.Ordinal))
        {
            ctx.Fail("issuer-mismatch");
        }
    }

    /// <summary>The ticket holds only iss, sub, name, issued_at (bounded sizes). No tokens.</summary>
    private static Task NormalizeTicket(TokenValidatedContext ctx, SubscribedSettings settings)
    {
        var sub = ctx.Principal?.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(sub) || sub.Length > MemberIdentity.MaxSubjectLength)
        {
            ctx.Fail("invalid-subject");
            return Task.CompletedTask;
        }

        var rawName = ctx.Principal?.FindFirst("name")?.Value;
        if (string.IsNullOrWhiteSpace(rawName))
        {
            rawName = ctx.Principal?.FindFirst("preferred_username")?.Value;
        }

        var now = ctx.Options.TimeProvider?.GetUtcNow() ?? DateTimeOffset.UtcNow;
        var identity = new ClaimsIdentity(
            [
                new Claim(SessionClaims.Issuer, settings.Issuer),
                new Claim(SessionClaims.Subject, sub),
                new Claim(SessionClaims.Name, SanitizeDisplayName(rawName)),
                new Claim(SessionClaims.IssuedAt, now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
            ],
            authenticationType: "subscribed",
            nameType: SessionClaims.Name,
            roleType: "role");
        ctx.Principal = new ClaimsPrincipal(identity);
        ctx.Properties!.IsPersistent = false;
        ctx.Properties.IssuedUtc = now;
        ctx.Properties.ExpiresUtc = now + SessionCookie.Idle;
        return Task.CompletedTask;
    }

    private static async Task OnTicketReceivedAsync(TicketReceivedContext ctx)
    {
        // Re-validate the return URL that travelled in the protected state.
        ctx.ReturnUri = ReturnUrl.IsValid(ctx.ReturnUri) ? ctx.ReturnUri : ReturnUrl.Default;
        if (ctx.Properties is { } properties)
        {
            var keep = properties.RedirectUri;
            foreach (var item in properties.Items.Keys.ToList())
            {
                properties.Items.Remove(item);
            }

            properties.RedirectUri = keep;
            properties.IsPersistent = false;
            var now = ctx.Options.TimeProvider?.GetUtcNow() ?? DateTimeOffset.UtcNow;
            properties.IssuedUtc = now;
            properties.ExpiresUtc = now + SessionCookie.Idle;
        }

        var outcome = "non-member";
        if (SessionClaims.MemberKey(ctx.Principal) is { } key)
        {
            try
            {
                var view = await ctx.HttpContext.RequestServices.GetRequiredService<HouseholdService>().GetViewAsync(ctx.HttpContext.RequestAborted);
                outcome = view?.FindMember(key.Issuer, key.Subject)?.MemberId ?? "non-member";
            }
            catch (Platform.EventStore.EventStoreException)
            {
                outcome = "membership-unknown";
            }
        }

        SecurityLog.Info(Logger(ctx.HttpContext), SecurityEvents.Login, outcome);
    }

    /// <summary>Control chars removed, trimmed, max 64 UTF-16 units without splitting a surrogate pair.</summary>
    public static string SanitizeDisplayName(string? raw)
    {
        var builder = new StringBuilder();
        foreach (var c in raw ?? "")
        {
            if (!char.IsControl(c))
            {
                builder.Append(c);
            }
        }

        var value = builder.ToString().Trim();
        if (value.Length > MaxDisplayNameLength)
        {
            var cut = MaxDisplayNameLength;
            if (char.IsHighSurrogate(value[cut - 1]))
            {
                cut--;
            }

            value = value[..cut].TrimEnd();
        }

        return value.Length == 0 ? DefaultDisplayName : value;
    }

    /// <summary>Failure kind only: never the exception message (it may echo protocol values).</summary>
    public static string ClassifyFailure(Exception? failure) => failure switch
    {
        null => "unknown",
        OpenIdConnectProtocolInvalidNonceException => "nonce-invalid",
        OpenIdConnectProtocolInvalidStateException => "state-invalid",
        OpenIdConnectProtocolException => "protocol-error",
        SecurityTokenException => "token-invalid",
        HttpRequestException => "idp-unreachable",
        _ when failure.Message.Contains("Correlation failed", StringComparison.Ordinal) => "correlation-failed",
        _ when failure.Message.Contains("oauth state", StringComparison.Ordinal) => "state-invalid",
        _ when failure.Message is "invalid-subject" or "issuer-mismatch" => failure.Message,
        _ => "other",
    };

    private static ILogger Logger(HttpContext context) =>
        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(SecurityEvents.Category);
}
