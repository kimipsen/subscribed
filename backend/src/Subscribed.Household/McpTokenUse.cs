using Marten;
using Microsoft.Extensions.Logging;
using Subscribed.Platform.EventStore;

namespace Subscribed.Household;

/// <summary>
/// When an MCP token was last used (plan D3). A plain Marten document, deliberately not an event: a last-used event would bump the
/// household version and cause spurious <c>If-Match</c> 412s on the household page. Holds no token, no hash and no member data.
/// </summary>
public sealed class McpTokenUse
{
    /// <summary>The public token id.</summary>
    public string Id { get; set; } = "";

    public DateTimeOffset LastUsedAt { get; set; }
}

/// <summary>Persistence seam for <see cref="McpTokenUse"/>. Tests substitute a throwing and a never-completing store.</summary>
public interface IMcpTokenUseStore
{
    Task UpsertAsync(string tokenId, DateTimeOffset lastUsedAt, CancellationToken ct);

    /// <summary>The last-used time of each listed token that has ever been used. Unused tokens are absent.</summary>
    Task<IReadOnlyDictionary<string, DateTimeOffset>> ListAsync(IReadOnlyCollection<string> tokenIds, CancellationToken ct);
}

public sealed class MartenMcpTokenUseStore(IDocumentStore store) : IMcpTokenUseStore
{
    public Task UpsertAsync(string tokenId, DateTimeOffset lastUsedAt, CancellationToken ct) =>
        EventStoreErrors.GuardAsync(async () =>
        {
            await using var session = store.LightweightSession();
            session.Store(new McpTokenUse { Id = tokenId, LastUsedAt = lastUsedAt.ToUniversalTime() });
            await session.SaveChangesAsync(ct).ConfigureAwait(false);
            return 0;
        });

    public Task<IReadOnlyDictionary<string, DateTimeOffset>> ListAsync(IReadOnlyCollection<string> tokenIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tokenIds);
        if (tokenIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyDictionary<string, DateTimeOffset>>(new Dictionary<string, DateTimeOffset>());
        }

        return EventStoreErrors.GuardAsync(async () =>
        {
            await using var session = store.QuerySession();
            var rows = await session.LoadManyAsync<McpTokenUse>(ct, [.. tokenIds]).ConfigureAwait(false);
            return (IReadOnlyDictionary<string, DateTimeOffset>)rows.ToDictionary(r => r.Id, r => r.LastUsedAt, StringComparer.Ordinal);
        });
    }
}

/// <summary>
/// Throttled, detached last-used writer. A touch writes at most once per token per <see cref="Window"/>, runs on its own
/// <see cref="Timeout"/> (on the injected <see cref="TimeProvider"/>), never captures the request or its token, and never lets an
/// exception escape (only the exception type is logged). The in-memory timestamp is recorded only after a successful upsert, so a failed
/// touch is retried by the next request. Concurrent touches for one token share one in-flight upsert.
/// </summary>
public sealed class McpTokenUseTracker(IMcpTokenUseStore store, TimeProvider time, ILogger<McpTokenUseTracker>? logger = null)
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _written = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _inFlight = new(StringComparer.Ordinal);

    /// <summary>
    /// Records a use of the token. Returns the shared in-flight touch (already complete when the throttle skipped it). The caller
    /// normally discards the task; <paramref name="ct"/> only abandons waiting for it and is never passed to the upsert.
    /// </summary>
    public Task TouchAsync(string tokenId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tokenId);
        Task touch;
        lock (_gate)
        {
            if (_written.TryGetValue(tokenId, out var last) && time.GetUtcNow() - last < Window)
            {
                return Task.CompletedTask;
            }

            if (!_inFlight.TryGetValue(tokenId, out touch!))
            {
                // Task.Run detaches from the caller's synchronization context and ExecutionContext flow of the request.
                _inFlight[tokenId] = touch = Task.Run(() => RunAsync(tokenId), CancellationToken.None);
            }
        }

        return touch.WaitAsync(ct);
    }

    /// <summary>Awaits every in-flight touch. Tests call it after a request; the host calls it on shutdown.</summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        Task[] pending;
        lock (_gate)
        {
            pending = [.. _inFlight.Values];
        }

        await Task.WhenAll(pending).WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Drops the in-memory entry of a revoked token. Its <see cref="McpTokenUse"/> row stays (the app role cannot delete).</summary>
    public void Forget(string tokenId)
    {
        lock (_gate)
        {
            _written.Remove(tokenId);
        }
    }

    public Task<IReadOnlyDictionary<string, DateTimeOffset>> ListUseAsync(IReadOnlyCollection<string> tokenIds, CancellationToken ct = default) =>
        store.ListAsync(tokenIds, ct);

    private async Task RunAsync(string tokenId)
    {
        try
        {
            using var cts = new CancellationTokenSource(Timeout, time);
            var usedAt = time.GetUtcNow();
            // WaitAsync abandons a store that ignores its token, so FlushAsync always returns once the timeout passes.
            await store.UpsertAsync(tokenId, usedAt, cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
            lock (_gate)
            {
                _written[tokenId] = usedAt;
            }
        }
        catch (Exception ex)
        {
            if (logger is not null)
            {
                McpTokenUseLog.TouchFailed(logger, tokenId, ex.GetType().Name);
            }
        }
        finally
        {
            lock (_gate)
            {
                _inFlight.Remove(tokenId);
            }
        }
    }
}

internal static partial class McpTokenUseLog
{
    [LoggerMessage(EventId = 3106, Level = LogLevel.Warning, Message = "household.mcp_token_use_failed {TokenId} {ExceptionType}")]
    private static partial void TouchFailedCore(ILogger logger, string tokenId, string exceptionType);

    /// <summary>A last-used write failed or timed out. Carries the token id and the exception type only, never the message.</summary>
    public static void TouchFailed(ILogger logger, string tokenId, string exceptionType) => TouchFailedCore(logger, tokenId, exceptionType);
}
