using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Subscribed.Platform.Problems;

/// <summary>RFC 9457 problem codes (design §7). Never include exception text, SQL or config values.</summary>
public static class ProblemCodes
{
    public const string Unauthenticated = "unauthenticated";
    public const string NotAHouseholdMember = "not-a-household-member";
    public const string CsrfHeaderRequired = "csrf-header-required";
    public const string OriginMismatch = "origin-mismatch";
    public const string ValidationFailed = "validation-failed";
    public const string ConcurrencyConflict = "concurrency-conflict";
    public const string IfMatchRequired = "if-match-required";
    public const string PayloadTooLarge = "payload-too-large";
    public const string DatabaseUnavailable = "database-unavailable";
    public const string InvalidReturnUrl = "invalid-return-url";
    public const string NotFound = "not-found";
    public const string MethodNotAllowed = "method-not-allowed";
    public const string UnsupportedMediaType = "unsupported-media-type";
    public const string InternalError = "internal-error";
}

public static class Problem
{
    public const string ContentType = "application/problem+json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string TraceId(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Activity.Current?.Id ?? context.TraceIdentifier;
    }

    public static async Task WriteAsync(HttpContext context, int status, string code, string title, IReadOnlyDictionary<string, object?>? extensions = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var body = new Dictionary<string, object?>
        {
            ["type"] = $"urn:subscribed:problem:{code}",
            ["title"] = title,
            ["status"] = status,
            ["traceId"] = TraceId(context),
        };
        if (extensions is not null)
        {
            foreach (var (key, value) in extensions)
            {
                body[key] = value;
            }
        }

        context.Response.StatusCode = status;
        context.Response.ContentType = ContentType;
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync(JsonSerializer.Serialize(body, Json), context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>An IResult for minimal API endpoints.</summary>
    public static IResult Result(int status, string code, string title, IReadOnlyDictionary<string, object?>? extensions = null) =>
        new ProblemResult(status, code, title, extensions);

    private sealed class ProblemResult(int status, string code, string title, IReadOnlyDictionary<string, object?>? extensions) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext) => WriteAsync(httpContext, status, code, title, extensions);
    }
}
