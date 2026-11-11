using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Subscribed.Api.Configuration;
using Subscribed.Household;
using Subscribed.Platform.Identity;
using Subscribed.Platform.Problems;

namespace Subscribed.Api.Bff;

public sealed record SessionUser(string DisplayName, string? MemberId = null, string? Subject = null);

public sealed record SessionResponse(string State, SessionUser? User = null, DateTimeOffset? ExpiresAt = null);

public sealed record LogoutResponse(string RedirectUrl, bool IdpLogout);

public static class BffEndpoints
{
    public const string LocalSignedOut = "/signed-out?idp=0";

    public static IEndpointRouteBuilder MapBffEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/bff/login", Login).AllowAnonymous();
        endpoints.MapGet("/bff/session", SessionAsync).AllowAnonymous();
        endpoints.MapPost("/bff/logout", LogoutAsync).AllowAnonymous();
        return endpoints;
    }

    private static IResult Login(HttpContext context, string? returnUrl)
    {
        context.Response.Headers.CacheControl = "no-store";
        var target = ReturnUrl.Normalize(returnUrl);
        if (target is null)
        {
            return Problem.Result(400, ProblemCodes.InvalidReturnUrl, "The return URL is not a local path.");
        }

        if (context.User.Identity?.IsAuthenticated == true)
        {
            return Results.Redirect(target);
        }

        return Results.Challenge(new AuthenticationProperties { RedirectUri = target }, [OidcSetup.Scheme]);
    }

    private static async Task<IResult> SessionAsync(HttpContext context, HouseholdService household)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (SessionClaims.MemberKey(context.User) is not { } key)
        {
            return Results.Json(new SessionResponse("anonymous"), JsonOptions);
        }

        var auth = await context.AuthenticateAsync(SessionCookie.Scheme);
        var expiresAt = auth.Properties?.ExpiresUtc;
        var displayName = context.User.FindFirst(SessionClaims.Name)?.Value ?? OidcSetup.DefaultDisplayName;

        // A database failure propagates (→ 503 database-unavailable); never answered as anonymous.
        var view = await household.GetViewAsync(context.RequestAborted);
        var member = view?.FindMember(key.Issuer, key.Subject);
        return member is not null
            ? Results.Json(new SessionResponse("member", new SessionUser(displayName, MemberId: member.MemberId), expiresAt), JsonOptions)
            : Results.Json(new SessionResponse("not-member", new SessionUser(displayName, Subject: key.Subject), expiresAt), JsonOptions);
    }

    private static async Task<IResult> LogoutAsync(HttpContext context, SubscribedSettings settings, IOptionsMonitor<OpenIdConnectOptions> oidcOptions, ILoggerFactory loggers)
    {
        context.Response.Headers.CacheControl = "no-store";
        var logger = loggers.CreateLogger(SecurityEvents.Category);
        var signedIn = context.User.Identity?.IsAuthenticated == true;

        // The local session always ends first, whatever happens with the IdP.
        if (signedIn)
        {
            await context.SignOutAsync(SessionCookie.Scheme);
            SessionCookie.MarkDeleted(context);
        }
        else
        {
            SessionCookie.Delete(context, settings);
        }

        SecurityLog.Info(logger, SecurityEvents.Logout, signedIn ? "signed-in" : "anonymous");

        if (!signedIn || settings.IdpLogout == IdpLogoutMode.None)
        {
            return Results.Json(new LogoutResponse(LocalSignedOut, false), JsonOptions);
        }

        try
        {
            var options = oidcOptions.Get(OidcSetup.Scheme);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var configuration = await options.ConfigurationManager!.GetConfigurationAsync(timeout.Token);
            var endSession = configuration.EndSessionEndpoint;
            if (Uri.TryCreate(endSession, UriKind.Absolute, out var endUri)
                && (endUri.Scheme == Uri.UriSchemeHttps || (settings.IsHttpOrigin && endUri.Scheme == Uri.UriSchemeHttp)))
            {
                var url = $"{endSession}{(endSession.Contains('?', StringComparison.Ordinal) ? '&' : '?')}client_id={Uri.EscapeDataString(settings.ClientId)}&post_logout_redirect_uri={Uri.EscapeDataString(settings.Origin + "/signed-out")}";
                return Results.Json(new LogoutResponse(url, true), JsonOptions);
            }

            SecurityLog.Warn(logger, SecurityEvents.IdpDiscoveryUnavailable, "invalid-logout-endpoint");
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException or OperationCanceledException)
        {
            SecurityLog.Warn(logger, SecurityEvents.IdpDiscoveryUnavailable, ex.GetType().Name);
        }

        return Results.Json(new LogoutResponse(LocalSignedOut, false), JsonOptions);
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };
}
