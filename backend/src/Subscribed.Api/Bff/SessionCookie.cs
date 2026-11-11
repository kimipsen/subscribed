using System.Globalization;
using System.Security.Cryptography;
using System.Xml;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Subscribed.Api.Configuration;
using Subscribed.Platform.Identity;
using Subscribed.Platform.EventStore;
using Subscribed.Platform.Problems;

namespace Subscribed.Api.Bff;

/// <summary>Session lifetime rules (design §6.3): 2 h idle (sliding), 12 h absolute.</summary>
public static class SessionCookie
{
    public const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;
    public static readonly TimeSpan Idle = TimeSpan.FromHours(2);
    public static readonly TimeSpan Absolute = TimeSpan.FromHours(12);
    public static readonly TimeSpan FutureSkew = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan RenewAfter = TimeSpan.FromMinutes(1);

    private const string ExpiredReasonKey = "subscribed.session.expired-reason";
    private const string DeletedKey = "subscribed.session.cookie-deleted";

    public static DateTimeOffset CappedExpiry(DateTimeOffset now, DateTimeOffset issuedAt)
    {
        var idle = now + Idle;
        var absolute = issuedAt + Absolute;
        return idle < absolute ? idle : absolute;
    }

    public static DateTimeOffset? IssuedAt(System.Security.Claims.ClaimsPrincipal? principal)
    {
        var raw = principal?.FindFirst(SessionClaims.IssuedAt)?.Value;
        return long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds is > 0 and < 99_999_999_999
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
    }

    /// <summary>OnValidatePrincipal: absolute limit, then sliding renewal capped at the absolute limit.</summary>
    public static Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var now = context.Options.TimeProvider!.GetUtcNow();
        var issuedAt = IssuedAt(context.Principal);
        if (issuedAt is null || issuedAt > now + FutureSkew || now >= issuedAt + Absolute)
        {
            context.HttpContext.Items[ExpiredReasonKey] = "absolute";
            context.RejectPrincipal();
            return Task.CompletedTask;
        }

        if (context.Properties.IssuedUtc is not { } lastIssued || now - lastIssued >= RenewAfter)
        {
            // The cookie handler re-issues with ExpiresUtc = now + (ExpiresUtc - IssuedUtc), so both
            // are set to make the renewed expiry exactly min(now + 2 h, issued_at + 12 h).
            context.Properties.IssuedUtc = now;
            context.Properties.ExpiresUtc = CappedExpiry(now, issuedAt.Value);
            context.ShouldRenew = true;
        }

        return Task.CompletedTask;
    }

    public static CookieOptions DeletionOptions(SubscribedSettings settings) => new()
    {
        Path = "/",
        HttpOnly = true,
        Secure = !settings.IsHttpOrigin,
        SameSite = SameSiteMode.Lax,
    };

    public static void Delete(HttpContext context, SubscribedSettings settings)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Items.ContainsKey(DeletedKey))
        {
            return;
        }

        context.Items[DeletedKey] = true;
        context.Response.Cookies.Delete(settings.SessionCookieName, DeletionOptions(settings));
    }

    public static void MarkDeleted(HttpContext context) => context.Items[DeletedKey] = true;

    /// <summary>
    /// Runs after authentication. A cookie that was sent but did not authenticate (idle/absolute
    /// expiry, tampering, unknown key) is deleted and the request continues anonymously. When the
    /// key ring itself cannot be loaded (database down), API/session requests get 503 instead of
    /// a success-shaped anonymous answer.
    /// </summary>
    public static async Task CleanupMiddleware(HttpContext context, RequestDelegate next, SubscribedSettings settings, IKeyManager keyManager, CookieAuthenticationOptions options, ILogger logger)
    {
        if (context.Request.Cookies.TryGetValue(settings.SessionCookieName, out var cookie) && context.User.Identity?.IsAuthenticated != true)
        {
            var result = await context.AuthenticateAsync(Scheme);
            string reason;
            if (context.Items[ExpiredReasonKey] is string r)
            {
                reason = r;
            }
            else if (string.Equals(result.Failure?.Message, "Ticket expired", StringComparison.Ordinal))
            {
                // A renewal caps ExpiresUtc at issued_at + 12 h, so an expired ticket may have hit
                // the absolute limit rather than the idle one.
                var issuedAt = IssuedAt(options.TicketDataFormat.Unprotect(cookie)?.Principal);
                reason = issuedAt is { } at && options.TimeProvider!.GetUtcNow() >= at + Absolute ? "absolute" : "idle";
            }
            else
            {
                reason = "invalid";
                if (!KeyRingAvailable(keyManager))
                {
                    SecurityLog.Warn(logger, SecurityEvents.DatabaseUnavailable, "key-ring");
                    if (RequestGuards.IsApiPath(context.Request.Path) || context.Request.Path.Equals("/bff/session", StringComparison.Ordinal))
                    {
                        await Problem.WriteAsync(context, 503, ProblemCodes.DatabaseUnavailable, "The service is temporarily unavailable.");
                        return;
                    }

                    await next(context);
                    return;
                }
            }

            SecurityLog.Info(logger, reason == "invalid" ? SecurityEvents.SessionInvalid : SecurityEvents.SessionExpired, $"reason={reason}");
            Delete(context, settings);
        }

        await next(context);
    }

    private static bool KeyRingAvailable(IKeyManager keyManager)
    {
        try
        {
            _ = keyManager.GetAllKeys();
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or XmlException or InvalidOperationException
            || EventStoreErrors.Map(ex) is not null)
        {
            return false;
        }
    }
}
