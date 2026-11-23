using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Subscribed.Household;
using Subscribed.Platform.Identity;

namespace Subscribed.Api.Mcp;

/// <summary>
/// The <c>McpBearer</c> scheme (plan D4). It reads only <c>Authorization: Bearer &lt;token&gt;</c> (no cookie, no query string), finds the
/// token in the household view and requires its member to be active. The 401 is NOT written here: the framework challenge is replaced by
/// <see cref="ProblemAuthorizationResultHandler"/>, so this handler only returns <c>NoResult</c> and never overrides <c>HandleChallenge</c>.
/// A database failure propagates (503); it is never turned into a 401.
/// </summary>
public sealed class McpBearerHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IHouseholdViewReader reader,
    McpTokenUseTracker tracker)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    public const string SchemeName = "McpBearer";
    public const string TokenIdClaim = "subscribed:mcp-token-id";
    private const string Prefix = "Bearer ";

    private readonly ILogger _log = loggerFactory.CreateLogger(McpLog.Category);

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization;
        if (header.Count == 0)
        {
            McpLog.AuthRejected(_log, McpLog.AuthMissing);
            return AuthenticateResult.NoResult();
        }

        var value = header.Count == 1 ? header[0] : null;
        var presented = value is not null && value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) ? value[Prefix.Length..].Trim() : null;
        if (!McpTokenSecrets.IsWellFormed(presented))
        {
            McpLog.AuthRejected(_log, McpLog.AuthMalformed);
            return AuthenticateResult.NoResult();
        }

        // A database failure propagates (503); it is never "unknown".
        var view = await reader.GetAsync(Context.RequestAborted).ConfigureAwait(false);
        var token = view is null ? null : McpTokenMatcher.Find(view.McpTokens, presented!);
        var member = token is null ? null : view!.Members.FirstOrDefault(m => string.Equals(m.MemberId, token.MemberId, StringComparison.Ordinal));
        if (token is null || member is null)
        {
            // A revoked token and a removed member's token both read as "unknown": the view drops them.
            McpLog.AuthRejected(_log, McpLog.AuthUnknown);
            return AuthenticateResult.NoResult();
        }

        // Detached on purpose: a failing or hanging last-used write never fails or delays the request.
        _ = tracker.TouchAsync(token.TokenId);
        var identity = new ClaimsIdentity(
            [new Claim(SessionClaims.Issuer, member.Issuer), new Claim(SessionClaims.Subject, member.Subject), new Claim(TokenIdClaim, token.TokenId)],
            SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
