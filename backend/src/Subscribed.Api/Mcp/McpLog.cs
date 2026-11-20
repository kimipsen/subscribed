namespace Subscribed.Api.Mcp;

/// <summary>
/// Content-free MCP observability (plan WP2 step 9). One line per tool call and one per 401 class. No line carries arguments,
/// results, the Authorization header, a token secret or a hash: only names, classes, codes and ids.
/// </summary>
public static partial class McpLog
{
    public const string Category = "Subscribed.Mcp";

    public const string OutcomeOk = "ok";
    public const string OutcomeDomainError = "domain-error";
    public const string OutcomeFailure = "failure";

    public const string AuthMissing = "missing";
    public const string AuthMalformed = "malformed";
    public const string AuthUnknown = "unknown";
    public const string AuthMemberInactive = "member-inactive";

    [LoggerMessage(EventId = 2000, Level = LogLevel.Information, Message = "mcp-tool {Tool} {Outcome} {ErrorCode} {TokenId} {CorrelationId}")]
    public static partial void ToolCall(ILogger logger, string tool, string outcome, string errorCode, string tokenId, string correlationId);

    [LoggerMessage(EventId = 2001, Level = LogLevel.Error, Message = "mcp-tool {Tool} failure {ExceptionType} {TokenId} {CorrelationId}")]
    public static partial void ToolFailure(ILogger logger, string tool, string exceptionType, string tokenId, string correlationId);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Information, Message = "mcp-auth {AuthClass}")]
    public static partial void AuthRejected(ILogger logger, string authClass);
}
