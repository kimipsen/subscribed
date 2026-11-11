using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Features;
using Subscribed.Api.Configuration;
using Subscribed.Platform.EventStore;
using Subscribed.Platform.Identity;
using Subscribed.Platform.Problems;

namespace Subscribed.Api.Bff;

/// <summary>Marker on the /api catch-all: unknown API paths are 404 before any other check.</summary>
public sealed class ApiNotFoundMarker;

public static class RequestGuards
{
    public const string CsrfHeader = "X-CSRF";
    public const string CspNonceKey = "subscribed.csp-nonce";

    public static bool IsApiPath(PathString path) =>
        path.Equals("/api", StringComparison.Ordinal) || path.StartsWithSegments("/api", StringComparison.Ordinal);

    private static bool RequiresCsrf(PathString path) =>
        IsApiPath(path) || path.Equals("/bff/session", StringComparison.Ordinal) || path.Equals("/bff/logout", StringComparison.Ordinal);

    private static bool RequiresOrigin(HttpRequest request) =>
        !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method) || HttpMethods.IsTrace(request.Method))
        && (IsApiPath(request.Path) || request.Path.Equals("/bff/logout", StringComparison.Ordinal));

    /// <summary>
    /// X-CSRF: 1 on /api/**, /bff/session, /bff/logout; exact Origin on unsafe methods there.
    /// /bff/login, /signin-oidc, /health/* and static files are exempt. No CORS policy exists.
    /// </summary>
    public static async Task CsrfMiddleware(HttpContext context, RequestDelegate next, SubscribedSettings settings, ILogger logger)
    {
        var request = context.Request;
        var endpoint = context.GetEndpoint();
        var knownApi = endpoint is RouteEndpoint route
            && endpoint.Metadata.GetMetadata<ApiNotFoundMarker>() is null
            && (route.RoutePattern.RawText ?? "").StartsWith("/api/", StringComparison.Ordinal);
        if (IsApiPath(request.Path) && !knownApi && endpoint?.DisplayName?.Contains("405", StringComparison.Ordinal) != true)
        {
            // Unknown API paths are a 404 problem, never the SPA and never 401/403.
            await Problem.WriteAsync(context, 404, ProblemCodes.NotFound, "Not found.");
            return;
        }

        if (RequiresCsrf(request.Path))
        {
            if (!string.Equals(request.Headers[CsrfHeader].ToString(), "1", StringComparison.Ordinal))
            {
                SecurityLog.Info(logger, SecurityEvents.CsrfRejected, request.Method);
                await Problem.WriteAsync(context, 403, ProblemCodes.CsrfHeaderRequired, "The X-CSRF header is required.");
                return;
            }

            if (RequiresOrigin(request) && request.Headers.Origin.Count > 0
                && !string.Equals(request.Headers.Origin.ToString(), settings.Origin, StringComparison.OrdinalIgnoreCase))
            {
                SecurityLog.Info(logger, SecurityEvents.OriginRejected, request.Method);
                await Problem.WriteAsync(context, 403, ProblemCodes.OriginMismatch, "The request origin is not allowed.");
                return;
            }
        }

        if (IsApiPath(request.Path) && context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = 16 * 1024;
        }

        await next(context);
    }

    /// <summary>Security headers on every response; CSP nonce per request; no-store on dynamic paths.</summary>
    public static Task SecurityHeadersMiddleware(HttpContext context, RequestDelegate next, SubscribedSettings settings)
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        context.Items[CspNonceKey] = nonce;
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers.ContentSecurityPolicy =
                $"default-src 'self'; script-src 'self'; style-src 'self' 'nonce-{nonce}'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
            headers.XFrameOptions = "DENY";
            if (!settings.IsHttpOrigin)
            {
                headers.StrictTransportSecurity = "max-age=31536000";
            }

            var path = context.Request.Path;
            if (string.IsNullOrEmpty(headers.CacheControl)
                && (IsApiPath(path) || path.StartsWithSegments("/bff", StringComparison.Ordinal) || path.StartsWithSegments("/health", StringComparison.Ordinal)
                    || path.StartsWithSegments(OidcSetup.CallbackPath, StringComparison.Ordinal) || headers.Location.Count > 0))
            {
                headers.CacheControl = "no-store";
            }

            return Task.CompletedTask;
        });
        return next(context);
    }

    /// <summary>
    /// Outermost error boundary: safe problem responses and logs that carry exception TYPE names
    /// only (messages may contain connection details or protocol values).
    /// </summary>
    public static async Task ErrorMiddleware(HttpContext context, RequestDelegate next, ILogger logger)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Client went away; nothing to answer.
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            await WriteIfPossible(context, 413, ProblemCodes.PayloadTooLarge, "The request body is too large.");
        }
#pragma warning disable CA1031 // This is the last-resort boundary.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var failure = ex is EventStoreException ese ? ese.Failure : EventStoreErrors.Map(ex);
            switch (failure?.Kind)
            {
                case EventStoreFailureKind.DatabaseUnavailable:
                    SecurityLog.Warn(logger, SecurityEvents.DatabaseUnavailable, ex.GetType().Name);
                    await WriteIfPossible(context, 503, ProblemCodes.DatabaseUnavailable, "The service is temporarily unavailable.");
                    break;
                case EventStoreFailureKind.PrivilegeMissing:
                    SecurityLog.Error(logger, SecurityEvents.PrivilegeMissing, failure.ObjectName ?? "unknown");
                    await WriteIfPossible(context, 500, ProblemCodes.InternalError, "An unexpected error occurred.");
                    break;
                case EventStoreFailureKind.ConcurrencyConflict:
                    await WriteIfPossible(context, 412, ProblemCodes.ConcurrencyConflict, "The household was changed by someone else.");
                    break;
                default:
                    SecurityLog.Error(logger, SecurityEvents.UnhandledError, ex.GetType().FullName ?? "exception");
                    await WriteIfPossible(context, 500, ProblemCodes.InternalError, "An unexpected error occurred.");
                    break;
            }
        }
    }

    private static async Task WriteIfPossible(HttpContext context, int status, string code, string title)
    {
        if (context.Response.HasStarted)
        {
            context.Abort();
            return;
        }

        context.Response.Clear();
        await Problem.WriteAsync(context, status, code, title);
    }

    /// <summary>Per-request log: method, route TEMPLATE (never the raw path/query), status, duration, traceId.</summary>
    public static async Task RequestLogMiddleware(HttpContext context, RequestDelegate next, ILogger logger)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            var route = context.GetEndpoint() switch
            {
                RouteEndpoint e => e.RoutePattern.RawText ?? "unknown",
                null => "static-or-unmatched",
                var e => e.DisplayName ?? "endpoint",
            };
            var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
            SecurityLog.Request(logger, context.Request.Method, route, context.Response.StatusCode, (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, traceId);
        }
    }
}
