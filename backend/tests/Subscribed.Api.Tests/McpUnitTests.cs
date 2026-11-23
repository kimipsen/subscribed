using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Subscribed.Api.Mcp;
using Subscribed.Household;
using Subscribed.Subscriptions;
using Subscribed.Subscriptions.Domain;
using Subscribed.TestSupport;
using Xunit;

namespace Subscribed.Api.Tests;

/// <summary>Unit tests for the MCP pieces that need no database: the constant-time matcher loop, the tool wrapper and the 401 handler.</summary>
public sealed class McpUnitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HouseholdViewMcpToken Token(string id, string hash) => new() { TokenId = id, MemberId = "m1", Name = "n", SecretHash = hash };

    [Fact]
    public void The_matcher_compares_against_every_stored_token_even_when_the_first_matches()
    {
        var calls = new List<string?>();
        var tokens = new[] { Token("a", "h1"), Token("b", "h2"), Token("c", "h3") };

        var found = McpTokenMatcher.Find(tokens, "secret", (_, hash) =>
        {
            calls.Add(hash);
            return hash == "h1";
        });

        Assert.Equal("a", found?.TokenId);
        Assert.Equal(["h1", "h2", "h3"], calls);
    }

    [Fact]
    public void The_matcher_returns_null_when_nothing_matches_and_still_compares_every_token()
    {
        var calls = 0;

        var found = McpTokenMatcher.Find([Token("a", "h1"), Token("b", "h2")], "secret", (_, _) =>
        {
            calls++;
            return false;
        });

        Assert.Null(found);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void The_matcher_uses_the_real_constant_time_comparison_by_default()
    {
        var secret = McpTokenSecrets.Create();

        var found = McpTokenMatcher.Find([Token("a", McpTokenSecrets.Hash(McpTokenSecrets.Create())), Token("b", McpTokenSecrets.Hash(secret))], secret);

        Assert.Equal("b", found?.TokenId);
    }

    private static string KeyOf(string field) =>
        (string)typeof(HouseholdMembership).GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;

    private static (McpToolRunner Runner, LogCapture Logs) CreateRunner(IMcpToolFault? fault = null)
    {
        var context = new DefaultHttpContext();
        context.Items[KeyOf("ViewKey")] = new HouseholdView();
        context.Items[KeyOf("MemberKey")] = "member-1";
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(McpBearerHandler.TokenIdClaim, "0123456789abcdef")], "McpBearer"));
        var logs = new LogCapture();
        var loggers = LoggerFactory.Create(b => b.AddProvider(logs));
        var service = new SubscriptionService(null!, TimeProvider.System, TestCatalog.Standard());
        return (new McpToolRunner(new HttpContextAccessor { HttpContext = context }, fault ?? new NoMcpToolFault(), service, loggers), logs);
    }

    [Fact]
    public async Task A_cancelled_request_is_rethrown_and_never_logged_as_a_failure()
    {
        var (runner, logs) = CreateRunner();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            runner.RunAsync("list_periods", null, _ => throw new OperationCanceledException(cts.Token), cts.Token));

        Assert.DoesNotContain(logs.Entries, e => e.Contains("failure", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Entries, e => e.Contains("mcp-tool", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_cancellation_that_is_not_the_request_token_is_an_unclassified_failure()
    {
        var (runner, logs) = CreateRunner();

        var result = await runner.RunAsync("list_periods", null, _ => throw new OperationCanceledException("inner timeout"), Ct);

        Assert.True(result.IsError);
        Assert.Single(logs.Entries, e => e.Contains("mcp-tool list_periods failure OperationCanceledException", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_unclassified_exception_is_one_failure_line_without_message_or_stack_and_a_generic_body()
    {
        var (runner, logs) = CreateRunner();

        var result = await runner.RunAsync("get_subscription", "abc", _ => throw new InvalidOperationException("SECRET-MESSAGE"), Ct);

        Assert.True(result.IsError);
        var text = ((ModelContextProtocol.Protocol.TextContentBlock)Assert.Single(result.Content)).Text;
        Assert.Contains("internal-error", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-MESSAGE", text, StringComparison.Ordinal);
        var line = Assert.Single(logs.Entries);
        Assert.Contains("mcp-tool get_subscription failure InvalidOperationException 0123456789abcdef", line, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-MESSAGE", line, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", line, StringComparison.Ordinal);
    }

    private sealed class Fault : IMcpToolFault
    {
        public void BeforeInvoke(string toolName) => throw new InvalidOperationException("injected");
    }

    [Fact]
    public async Task The_fault_seam_runs_inside_the_wrapper_before_the_body()
    {
        var (runner, logs) = CreateRunner(new Fault());
        var bodyRan = false;

        var result = await runner.RunAsync("list_periods", null, _ =>
        {
            bodyRan = true;
            return Task.FromResult(McpResults.Ok(new { }));
        }, Ct);

        Assert.True(result.IsError);
        Assert.False(bodyRan);
        Assert.Single(logs.Entries, e => e.Contains("mcp-tool list_periods failure InvalidOperationException", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_domain_failure_is_a_tool_error_with_the_web_api_code_and_an_ok_path_is_logged_as_ok()
    {
        var (runner, logs) = CreateRunner();
        var errors = new Dictionary<string, string[]> { ["name"] = ["Name is required."] };

        var invalid = await runner.RunAsync("get_subscription", "abc", _ => throw new SubscriptionValidationException(errors), Ct);
        var missing = await runner.RunAsync("get_subscription", "abc", _ => throw new SubscriptionNotFoundException(), Ct);
        var ok = await runner.RunAsync("list_periods", null, _ => Task.FromResult(McpResults.Ok(new { fine = true })), Ct);

        Assert.True(invalid.IsError);
        Assert.Contains("\"code\":\"validation-failed\"", ((ModelContextProtocol.Protocol.TextContentBlock)invalid.Content[0]).Text, StringComparison.Ordinal);
        Assert.Contains("Name is required.", ((ModelContextProtocol.Protocol.TextContentBlock)invalid.Content[0]).Text, StringComparison.Ordinal);
        Assert.Contains("\"code\":\"not-found\"", ((ModelContextProtocol.Protocol.TextContentBlock)missing.Content[0]).Text, StringComparison.Ordinal);
        Assert.NotEqual(true, ok.IsError);
        Assert.Equal(3, logs.Entries.Count);
        Assert.Contains("domain-error validation-failed", logs.Entries[0], StringComparison.Ordinal);
        Assert.Contains("domain-error not-found", logs.Entries[1], StringComparison.Ordinal);
        Assert.Contains("mcp-tool list_periods ok", logs.Entries[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_authenticated_principal_whose_requirement_failed_on_mcp_is_the_same_401_and_logs_member_inactive()
    {
        var logs = new LogCapture();
        var handler = new ProblemAuthorizationResultHandler(LoggerFactory.Create(b => b.AddProvider(logs)));
        var context = new DefaultHttpContext();
        context.Request.Path = "/mcp";
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("iss", "i"), new Claim("sub", "s")], "McpBearer"));
        var policy = new AuthorizationPolicyBuilder("McpBearer").RequireAuthenticatedUser().Build();

        await handler.HandleAsync(_ => Task.CompletedTask, context, policy, PolicyAuthorizationResult.Forbid());

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal("Bearer", context.Response.Headers.WWWAuthenticate.ToString());
        Assert.Equal("application/problem+json", context.Response.ContentType);
        Assert.Single(logs.Entries, e => e.Contains("mcp-auth member-inactive", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_web_policy_failure_keeps_its_401_and_403_and_adds_no_challenge_header()
    {
        var handler = new ProblemAuthorizationResultHandler();
        var policy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();

        var anonymous = new DefaultHttpContext();
        anonymous.Request.Path = "/api/subscriptions";
        anonymous.Response.Body = new MemoryStream();
        await handler.HandleAsync(_ => Task.CompletedTask, anonymous, policy, PolicyAuthorizationResult.Challenge());

        var member = new DefaultHttpContext();
        member.Request.Path = "/api/subscriptions";
        member.Response.Body = new MemoryStream();
        member.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("iss", "i")], "Cookies"));
        await handler.HandleAsync(_ => Task.CompletedTask, member, policy, PolicyAuthorizationResult.Forbid());

        Assert.Equal(StatusCodes.Status401Unauthorized, anonymous.Response.StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, member.Response.StatusCode);
        Assert.False(anonymous.Response.Headers.ContainsKey("WWW-Authenticate"));
        Assert.False(member.Response.Headers.ContainsKey("WWW-Authenticate"));
    }
}
