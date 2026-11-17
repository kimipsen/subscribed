using System.Text;
using Subscribed.Household;
using Subscribed.Household.Domain;
using Subscribed.Platform.Problems;
using Subscribed.Subscriptions;
using Subscribed.Subscriptions.Calendar;

namespace Subscribed.Api.Calendar;

/// <summary>
/// The anonymous iCal feed (plan 4.4, D7, D8): <c>GET|HEAD /calendar/{secret}/subscribed.ics</c>. Calendar apps cannot send the
/// BFF cookie or X-CSRF, so the unguessable secret is the credential. Every failure is the same 404; the secret is never logged
/// (the request log carries the route template, not the path).
/// </summary>
public static class CalendarEndpoints
{
    private const string FeedContentType = "text/calendar; charset=utf-8";
    public const string RouteTemplate = "/calendar/{secret}/subscribed.ics";

    public static IEndpointRouteBuilder MapCalendarEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapMethods(RouteTemplate, [HttpMethods.Get, HttpMethods.Head], FeedAsync).AllowAnonymous();
        return endpoints;
    }

    private static async Task<IResult> FeedAsync(
        string secret,
        HttpContext context,
        HouseholdService households,
        SubscriptionService subscriptions,
        TimeProvider time,
        ILoggerFactory loggers)
    {
        // Junk paths cost no query: only a 43-character base64url secret can ever match.
        if (!CalendarFeedSecrets.IsWellFormed(secret))
        {
            return NotFound(context);
        }

        // A database failure propagates (503); it is never reported as a wrong secret.
        var view = await households.GetViewAsync(context.RequestAborted).ConfigureAwait(false);
        if (view is null || !CalendarFeedSecrets.Matches(secret, view.CalendarFeedSecretHash))
        {
            return NotFound(context);
        }

        var zone = TimeZoneInfo.Utc;
        var zoneId = TimeZoneIds.Utc;
        if (TimeZoneIds.TryResolve(view.TimeZoneId, out var resolved))
        {
            zone = resolved;
            zoneId = view.TimeZoneId;
        }
        else
        {
            HouseholdLog.TimeZoneUnresolvable(loggers.CreateLogger(HouseholdLog.Category), HouseholdService.StreamId);
        }

        var headers = context.Response.Headers;
        headers.CacheControl = "no-store";
        headers["X-Robots-Tag"] = "noindex";
        headers.ContentDisposition = "inline; filename=\"subscribed.ics\"";
        if (HttpMethods.IsHead(context.Request.Method))
        {
            // Same headers, no body, and no feed generation: HEAD only proves the link is alive.
            context.Response.ContentType = FeedContentType;
            return Results.Empty;
        }

        var all = await subscriptions.ListAsync(context.RequestAborted).ConfigureAwait(false);
        var text = CalendarFeedBuilder.Build(all, zone, zoneId, context.Request.Query["lang"].ToString(), time.GetUtcNow());

        return Results.Content(text, FeedContentType, Encoding.UTF8);
    }

    /// <summary>The one answer for a wrong, revoked, rotated-out or malformed secret: the same as any unknown route.</summary>
    private static IResult NotFound(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        return Problem.Result(404, ProblemCodes.NotFound, "Not found.");
    }
}
