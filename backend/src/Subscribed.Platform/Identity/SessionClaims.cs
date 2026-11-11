using System.Security.Claims;

namespace Subscribed.Platform.Identity;

/// <summary>The only claims a session ticket carries (design §6.3).</summary>
public static class SessionClaims
{
    public const string Issuer = "iss";
    public const string Subject = "sub";
    public const string Name = "name";
    public const string IssuedAt = "issued_at";

    public static (string Issuer, string Subject)? MemberKey(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var iss = principal.FindFirst(Issuer)?.Value;
        var sub = principal.FindFirst(Subject)?.Value;
        return string.IsNullOrEmpty(iss) || string.IsNullOrEmpty(sub) ? null : (iss, sub);
    }
}

/// <summary>Stable names for security log events. Values are safe: no tokens, cookies, codes or queries.</summary>
public static class SecurityEvents
{
    public const string Category = "Subscribed.Security";

    public const string Login = "login";
    public const string CallbackFailed = "callback-failed";
    public const string AccessDenied = "access-denied";
    public const string Logout = "logout";
    public const string CsrfRejected = "csrf-rejected";
    public const string OriginRejected = "origin-rejected";
    public const string ConcurrencyConflict = "concurrency-conflict";
    public const string SessionExpired = "session-expired";
    public const string SessionInvalid = "session-invalid";
    public const string PrivilegeMissing = "database-privilege-missing";
    public const string DatabaseUnavailable = "database-unavailable";
    public const string IdpDiscoveryUnavailable = "idp-discovery-unavailable";
    public const string UnhandledError = "unhandled-error";
}
