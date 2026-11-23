using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Subscribed.Household.Domain;
using Subscribed.Platform.EventStore;
using Subscribed.Platform.Problems;

namespace Subscribed.Household;

/// <summary>
/// The MCP feature switch (plan D9, <c>Mcp:Enabled</c>, default true). Off stops new token creation; listing and revoking stay
/// available so a member can always revoke.
/// </summary>
public sealed record McpOptions(bool Enabled = true);

public sealed record McpTokenDto(string Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt)
{
    public static McpTokenDto From(McpTokenSummary token) => new(token.Id, token.Name, token.CreatedAt, token.LastUsedAt);
}

public sealed record McpTokenListDto(IReadOnlyList<McpTokenDto> Tokens);

/// <summary>The create response: the updated household (fresh <c>version</c> for <c>If-Match</c>), the token and its one-time secret.</summary>
public sealed record McpTokenCreatedDto(HouseholdDto Household, McpTokenDto Token, string Secret);

public sealed record McpTokenRevokedDto(HouseholdDto Household);

public sealed record CreateMcpTokenRequest(string? Name);

/// <summary>
/// Token management for the signed-in member, on the cookie scheme only (normal <c>X-CSRF</c> and <c>Origin</c> rules apply; an MCP
/// token is never accepted here). The member id always comes from the membership check, never from the body.
/// </summary>
public static class McpTokenEndpoints
{
    public const string Path = "/api/household/mcp-tokens";

    public static IEndpointRouteBuilder MapMcpTokenEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup(Path).RequireAuthorization(HouseholdMembership.PolicyName);
        group.MapGet("", ListAsync);
        group.MapPost("", CreateAsync);
        group.MapDelete("{tokenId}", RevokeAsync);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(HttpContext context, McpTokenUseTracker tracker)
    {
        var view = HouseholdMembership.ViewFor(context);
        var memberId = HouseholdMembership.MemberIdFor(context);
        var own = view.McpTokens.Where(t => t.MemberId == memberId).OrderBy(t => t.CreatedAt).ThenBy(t => t.TokenId, StringComparer.Ordinal).ToList();
        var use = await tracker.ListUseAsync([.. own.Select(t => t.TokenId)], context.RequestAborted).ConfigureAwait(false);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(new McpTokenListDto(
            [.. own.Select(t => new McpTokenDto(t.TokenId, t.Name, t.CreatedAt, use.TryGetValue(t.TokenId, out var at) ? at : null))]));
    }

    private static async Task<IResult> CreateAsync(HttpContext context, HouseholdService service, McpOptions options, ILoggerFactory loggers)
    {
        var memberId = HouseholdMembership.MemberIdFor(context);
        if (!options.Enabled)
        {
            return Problem.Result(409, ProblemCodes.McpDisabled, "MCP access is disabled on this server.");
        }

        var body = await HouseholdEndpoints.ReadBodyAsync<CreateMcpTokenRequest>(context, "name", "Token name must be 1–64 characters.").ConfigureAwait(false);
        if (body.Error is { } error)
        {
            return error;
        }

        try
        {
            var (view, token, secret) = await service.CreateMcpTokenAsync(memberId, body.Request!.Name, WriteContext.Member(memberId, Problem.TraceId(context)), context.RequestAborted).ConfigureAwait(false);
            context.Response.Headers.ETag = HouseholdEndpoints.ETag(view.Version);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(new McpTokenCreatedDto(HouseholdDto.From(view), McpTokenDto.From(token), secret), statusCode: StatusCodes.Status201Created);
        }
        catch (HouseholdDomainException ex) when (ex.Code == HouseholdErrorCodes.TooManyTokens)
        {
            return Problem.Result(409, ProblemCodes.TooManyTokens, ex.Message);
        }
        catch (HouseholdDomainException ex)
        {
            return HouseholdEndpoints.ValidationProblem(ex.Field, ex.Message);
        }
        catch (EventStoreException ex) when (ex.Failure.Kind == EventStoreFailureKind.ConcurrencyConflict)
        {
            return await HouseholdEndpoints.ConflictProblemAsync(context, service, loggers, memberId).ConfigureAwait(false);
        }
    }

    private static async Task<IResult> RevokeAsync(string tokenId, HttpContext context, HouseholdService service, McpTokenUseTracker tracker, ILoggerFactory loggers)
    {
        var memberId = HouseholdMembership.MemberIdFor(context);
        try
        {
            var view = await service.RevokeMcpTokenAsync(memberId, tokenId, WriteContext.Member(memberId, Problem.TraceId(context)), context.RequestAborted).ConfigureAwait(false);
            tracker.Forget(tokenId);
            return Results.Json(new McpTokenRevokedDto(HouseholdDto.From(view))).WithNoStoreAndETag(context, view);
        }
        catch (HouseholdDomainException ex) when (ex.Code is HouseholdErrorCodes.TokenNotFound or HouseholdErrorCodes.NotAMember)
        {
            return Problem.Result(404, ProblemCodes.NotFound, "Token not found.");
        }
        catch (EventStoreException ex) when (ex.Failure.Kind == EventStoreFailureKind.ConcurrencyConflict)
        {
            return await HouseholdEndpoints.ConflictProblemAsync(context, service, loggers, memberId).ConfigureAwait(false);
        }
    }

    private static IResult WithNoStoreAndETag(this IResult result, HttpContext context, HouseholdView view)
    {
        context.Response.Headers.ETag = HouseholdEndpoints.ETag(view.Version);
        context.Response.Headers.CacheControl = "no-store";
        return result;
    }
}
