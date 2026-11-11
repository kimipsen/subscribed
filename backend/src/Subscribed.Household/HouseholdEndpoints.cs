using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Subscribed.Household.Domain;
using Subscribed.Platform.EventStore;
using Subscribed.Platform.Identity;
using Subscribed.Platform.Problems;

namespace Subscribed.Household;

public sealed record HouseholdMemberDto(string MemberId, string DisplayLabel, DateTimeOffset GrantedAt);

/// <summary>Never exposes other members' issuer/subject.</summary>
public sealed record HouseholdDto(string Id, string Name, long Version, IReadOnlyList<HouseholdMemberDto> Members)
{
    public static HouseholdDto From(HouseholdView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        return new(view.Id, view.Name, view.Version, view.Members.Select(m => new HouseholdMemberDto(m.MemberId, m.DisplayLabel, m.GrantedAt)).ToList());
    }
}

public sealed record RenameRequest(string? Name);

public static class HouseholdEndpoints
{
    public const int MaxBodyBytes = 16 * 1024;

    public static IEndpointRouteBuilder MapHouseholdEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/household").RequireAuthorization(HouseholdMembership.PolicyName);
        group.MapGet("", GetAsync);
        group.MapPut("/name", RenameAsync);
        return endpoints;
    }

    private static IResult GetAsync(HttpContext context)
    {
        var view = HouseholdMembership.ViewFor(context);
        context.Response.Headers.ETag = ETag(view.Version);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(HouseholdDto.From(view));
    }

    private static async Task<IResult> RenameAsync(HttpContext context, HouseholdService service, ILoggerFactory loggers)
    {
        var view = HouseholdMembership.ViewFor(context);
        var memberId = HouseholdMembership.MemberIdFor(context);

        var ifMatch = context.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(ifMatch))
        {
            return Problem.Result(428, ProblemCodes.IfMatchRequired, "An If-Match header with the current version is required.");
        }

        if (!TryParseETag(ifMatch, out var expectedVersion))
        {
            return ValidationProblem("If-Match", "If-Match must be a quoted version, for example \"3\".");
        }

        var body = await ReadBodyAsync(context).ConfigureAwait(false);
        if (body.Error is { } error)
        {
            return error;
        }

        string name;
        try
        {
            name = Domain.Household.NormalizeName(body.Request!.Name);
        }
        catch (HouseholdDomainException ex)
        {
            return ValidationProblem(ex.Field, ex.Message);
        }

        try
        {
            var updated = await service.RenameAsync(expectedVersion, name, WriteContext.Member(memberId, Problem.TraceId(context)), context.RequestAborted).ConfigureAwait(false);
            context.Response.Headers.ETag = ETag(updated.Version);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(HouseholdDto.From(updated));
        }
        catch (EventStoreException ex) when (ex.Failure.Kind == EventStoreFailureKind.ConcurrencyConflict)
        {
            SecurityLog.Info(loggers.CreateLogger(SecurityEvents.Category), SecurityEvents.ConcurrencyConflict, memberId);
            var current = await service.GetViewAsync(context.RequestAborted).ConfigureAwait(false);
            return Problem.Result(412, ProblemCodes.ConcurrencyConflict, "The household was changed by someone else.",
                new Dictionary<string, object?> { ["currentVersion"] = current?.Version ?? view.Version });
        }
    }

    public static string ETag(long version) => $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";

    public static bool TryParseETag(string value, out long version)
    {
        version = 0;
        var v = (value ?? "").Trim();
        return v.Length >= 3 && v[0] == '"' && v[^1] == '"'
            && long.TryParse(v.AsSpan(1, v.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out version);
    }

    private static IResult ValidationProblem(string field, string message) =>
        Problem.Result(400, ProblemCodes.ValidationFailed, "Validation failed",
            new Dictionary<string, object?> { ["errors"] = new Dictionary<string, string[]> { [JsonNamingPolicy.CamelCase.ConvertName(field)] = [message] } });

    private static async Task<(RenameRequest? Request, IResult? Error)> ReadBodyAsync(HttpContext context)
    {
        if (context.Request.ContentLength > MaxBodyBytes)
        {
            return (null, Problem.Result(413, ProblemCodes.PayloadTooLarge, "The request body is too large."));
        }

        if (!context.Request.HasJsonContentType())
        {
            return (null, Problem.Result(415, ProblemCodes.UnsupportedMediaType, "The request body must be application/json."));
        }

        var buffer = new byte[MaxBodyBytes + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await context.Request.Body.ReadAsync(buffer.AsMemory(total), context.RequestAborted).ConfigureAwait(false)) > 0)
        {
            total += read;
        }

        if (total > MaxBodyBytes)
        {
            return (null, Problem.Result(413, ProblemCodes.PayloadTooLarge, "The request body is too large."));
        }

        try
        {
            var request = JsonSerializer.Deserialize<RenameRequest>(buffer.AsSpan(0, total), HouseholdEventTypes.Json);
            return request is null ? (null, ValidationProblem("name", "Name must be 1–80 characters.")) : (request, null);
        }
        catch (JsonException)
        {
            return (null, ValidationProblem("name", "Name must be 1–80 characters."));
        }
    }
}

/// <summary>Authenticated is not authorized: (iss, sub) must be an active member, checked every request.</summary>
public sealed class HouseholdMemberRequirement : IAuthorizationRequirement;

public sealed class HouseholdMembership(HouseholdService service, ILoggerFactory loggers) : AuthorizationHandler<HouseholdMemberRequirement>
{
    public const string PolicyName = "HouseholdMember";
    private const string ViewKey = "subscribed.household.view";
    private const string MemberKey = "subscribed.household.member";

    public static HouseholdView ViewFor(HttpContext context) =>
        context.Items[ViewKey] as HouseholdView ?? throw new InvalidOperationException("Membership was not evaluated.");

    public static string MemberIdFor(HttpContext context) =>
        context.Items[MemberKey] as string ?? throw new InvalidOperationException("Membership was not evaluated.");

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, HouseholdMemberRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (SessionClaims.MemberKey(context.User) is not { } key || context.Resource is not HttpContext http)
        {
            return;
        }

        // Database failures propagate (→ 503); they are never turned into "not a member".
        var view = await service.GetViewAsync(http.RequestAborted).ConfigureAwait(false);
        var member = view?.FindMember(key.Issuer, key.Subject);
        if (view is null || member is null)
        {
            SecurityLog.Info(loggers.CreateLogger(SecurityEvents.Category), SecurityEvents.AccessDenied, key.Subject);
            return;
        }

        http.Items[ViewKey] = view;
        http.Items[MemberKey] = member.MemberId;
        context.Succeed(requirement);
    }
}
