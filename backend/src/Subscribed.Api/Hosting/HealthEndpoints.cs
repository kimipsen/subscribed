using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Subscribed.Api.Bff;
using Subscribed.Api.Configuration;
using Subscribed.Platform.Database;
using Subscribed.Platform.Identity;

namespace Subscribed.Api.Hosting;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health/live", () => Results.Json(new { status = "Healthy" })).AllowAnonymous();
        endpoints.MapGet("/health/ready", ReadyAsync).AllowAnonymous();
        return endpoints;
    }

    /// <summary>Per-check status only. The identity provider may be Degraded without failing readiness.</summary>
    private static async Task<IResult> ReadyAsync(HttpContext context, SubscribedSettings settings, IOptionsMonitor<OpenIdConnectOptions> oidc, ILoggerFactory loggers)
    {
        var db = await ReadinessProbe.CheckAsync(settings.AppConnectionString, settings.Schema, context.RequestAborted);
        var logger = loggers.CreateLogger(SecurityEvents.Category);
        var idp = await IdentityProviderStatusAsync(oidc.Get(OidcSetup.Scheme), logger, context.RequestAborted);
        var ready = db.Database && db.Schema && db.Privileges;
        if (!ready)
        {
            foreach (var problem in db.Problems.Take(20))
            {
                SecurityLog.Warn(logger, "readiness-failed", problem);
            }
        }

        static string S(bool ok) => ok ? "Healthy" : "Unhealthy";
        var body = new
        {
            status = ready ? (idp == "Healthy" ? "Healthy" : "Degraded") : "Unhealthy",
            checks = new Dictionary<string, string>
            {
                ["database"] = S(db.Database),
                ["schema"] = S(db.Schema),
                ["privileges"] = S(db.Privileges),
                ["identityProvider"] = idp,
            },
        };
        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(body, statusCode: ready ? 200 : 503);
    }

    private static async Task<string> IdentityProviderStatusAsync(OpenIdConnectOptions options, ILogger logger, CancellationToken ct)
    {
        if (options.ConfigurationManager is null)
        {
            SecurityLog.Warn(logger, SecurityEvents.IdpDiscoveryUnavailable, "missing-configuration-manager");
            return "Degraded";
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await options.ConfigurationManager.GetConfigurationAsync(timeout.Token);
            return "Healthy";
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException or OperationCanceledException)
        {
            SecurityLog.Warn(logger, SecurityEvents.IdpDiscoveryUnavailable, ex.GetType().Name);
            return "Degraded";
        }
    }
}
