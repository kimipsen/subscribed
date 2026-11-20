using System.Text.Json;
using ModelContextProtocol.Protocol;
using Subscribed.Platform.Problems;
using Subscribed.Subscriptions;

namespace Subscribed.Api.Mcp;

/// <summary>A tool answer plus the error code the observability line records (null for success).</summary>
public sealed record McpToolResult(CallToolResult Result, string? ErrorCode);

/// <summary>Builds tool answers. A tool never throws for a domain error and never exposes a stack trace or an exception message.</summary>
public static class McpResults
{
    /// <summary>The same options <c>Results.Json</c> uses in the web API (camelCase, web defaults), so a DTO serializes identically.</summary>
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static McpToolResult Ok(object value) =>
        new(new CallToolResult { Content = [new TextContentBlock { Text = JsonSerializer.Serialize(value, value.GetType(), Json) }] }, null);

    public static McpToolResult Error(string code, string title, IDictionary<string, string[]>? errors = null, long? currentVersion = null, string? traceId = null)
    {
        var body = new Dictionary<string, object?> { ["code"] = code, ["title"] = title };
        if (errors is not null)
        {
            body["errors"] = errors;
        }

        if (currentVersion is { } version)
        {
            body["currentVersion"] = version;
        }

        if (traceId is not null)
        {
            body["traceId"] = traceId;
        }

        return new(new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = JsonSerializer.Serialize(body, Json) }] }, code);
    }

    public static McpToolResult Error(SubscriptionFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return Error(failure.Code, failure.Title, failure.Errors, failure.CurrentVersion);
    }

    public static McpToolResult Validation(string field, string message) =>
        Error(ProblemCodes.ValidationFailed, "Validation failed", new Dictionary<string, string[]> { [field] = [message] });

    public static McpToolResult InternalError(string traceId) =>
        Error(ProblemCodes.InternalError, "The request failed.", traceId: traceId);
}
