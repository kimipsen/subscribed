using Microsoft.AspNetCore.Http;
using Subscribed.Subscriptions;

namespace Subscribed.Api.Mcp;

/// <summary>
/// Fault-injection seam (plan WP2 step 6): called inside the runner's <c>try</c> immediately before a tool body. The default does
/// nothing; the test host replaces it with one that throws for a named tool, which exercises exactly the catch, log and generic-body path.
/// </summary>
public interface IMcpToolFault
{
    void BeforeInvoke(string toolName);
}

public sealed class NoMcpToolFault : IMcpToolFault
{
    public void BeforeInvoke(string toolName)
    {
    }
}

/// <summary>
/// The one wrapper around every tool body. Domain failures become tool errors with the web API's codes; anything else is logged once
/// (type name only, no message, no stack) and answered with a fixed generic error. A request cancellation is rethrown.
/// </summary>
public sealed class McpToolRunner
{
    private readonly IHttpContextAccessor _accessor;
    private readonly IMcpToolFault _fault;
    private readonly SubscriptionService _subscriptions;
    private readonly ILogger _log;

    public McpToolRunner(IHttpContextAccessor accessor, IMcpToolFault fault, SubscriptionService subscriptions, ILoggerFactory loggers)
    {
        _accessor = accessor;
        _fault = fault;
        _subscriptions = subscriptions;
        _log = loggers.CreateLogger(McpLog.Category);
    }

    /// <param name="tool">The tool name, recorded in the outcome line.</param>
    /// <param name="subscriptionId">The bare subscription id a state or conflict failure reads the current version of; null for tools without one.</param>
    /// <param name="body">The tool body. It may throw the subscription domain exceptions.</param>
    /// <param name="ct">The request cancellation token.</param>
    public async Task<ModelContextProtocol.Protocol.CallToolResult> RunAsync(string tool, string? subscriptionId, Func<McpCaller, Task<McpToolResult>> body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        var caller = McpCaller.From(_accessor);
        try
        {
            _fault.BeforeInvoke(tool);
            var result = await body(caller).ConfigureAwait(false);
            McpLog.ToolCall(_log, tool, result.ErrorCode is null ? McpLog.OutcomeOk : McpLog.OutcomeDomainError, result.ErrorCode ?? "", caller.TokenId, caller.CorrelationId);
            return result.Result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Every other exception is deliberately reduced to the generic tool error.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var failure = await TryMapAsync(ex, subscriptionId, ct).ConfigureAwait(false);
            if (failure.Mapped is { } mapped)
            {
                McpLog.ToolCall(_log, tool, McpLog.OutcomeDomainError, mapped.Code, caller.TokenId, caller.CorrelationId);
                return McpResults.Error(mapped).Result;
            }

            McpLog.ToolFailure(_log, tool, failure.Type, caller.TokenId, caller.CorrelationId);
            return McpResults.InternalError(caller.CorrelationId).Result;
        }
    }

    private async Task<(SubscriptionFailure? Mapped, string Type)> TryMapAsync(Exception ex, string? subscriptionId, CancellationToken ct)
    {
        try
        {
            return (await SubscriptionFailures.MapAsync(ex, subscriptionId ?? "", _subscriptions, ct).ConfigureAwait(false), ex.GetType().Name);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Mapping itself failed (for example the current-version read): it is an unclassified failure.
        catch (Exception mappingFailure)
#pragma warning restore CA1031
        {
            return (null, mappingFailure.GetType().Name);
        }
    }
}
