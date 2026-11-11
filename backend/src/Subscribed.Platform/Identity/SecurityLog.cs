using Microsoft.Extensions.Logging;

namespace Subscribed.Platform.Identity;

/// <summary>
/// Structured security/operations log entries. <c>detail</c> must be a safe value: a member id,
/// subject, reason keyword, object name, or exception TYPE name; never a message, token, cookie,
/// code, query string, connection string or request body.
/// </summary>
public static partial class SecurityLog
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Information, Message = "security {SecurityEvent} {Detail}")]
    public static partial void Info(ILogger logger, string securityEvent, string detail);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning, Message = "security {SecurityEvent} {Detail}")]
    public static partial void Warn(ILogger logger, string securityEvent, string detail);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Error, Message = "failure {SecurityEvent} {Detail}")]
    public static partial void Error(ILogger logger, string securityEvent, string detail);

    [LoggerMessage(EventId = 1100, Level = LogLevel.Information, Message = "request {Method} {Route} {StatusCode} {DurationMs} {TraceId}")]
    public static partial void Request(ILogger logger, string method, string route, int statusCode, long durationMs, string traceId);
}
