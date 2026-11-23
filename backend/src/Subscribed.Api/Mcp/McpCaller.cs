using Microsoft.AspNetCore.Http;
using Subscribed.Household;
using Subscribed.Platform.Problems;

namespace Subscribed.Api.Mcp;

/// <summary>Who is calling: the member, the token and the household view the per-request membership check read (HouseholdMembership, TS-002).</summary>
public sealed record McpCaller(string MemberId, string TokenId, HouseholdView View, string CorrelationId)
{
    public static McpCaller From(IHttpContextAccessor accessor)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        var context = accessor.HttpContext ?? throw new InvalidOperationException("No HTTP context.");
        return new McpCaller(
            HouseholdMembership.MemberIdFor(context),
            context.User.FindFirst(McpBearerHandler.TokenIdClaim)?.Value ?? "",
            HouseholdMembership.ViewFor(context),
            Problem.TraceId(context));
    }
}
