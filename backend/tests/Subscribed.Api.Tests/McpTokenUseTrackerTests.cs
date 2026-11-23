using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Subscribed.Household;
using Subscribed.TestSupport;
using Xunit;

namespace Subscribed.Api.Tests;

/// <summary>The throttled, detached last-used tracker. FakeTimeProvider and FlushAsync only: no sleeps.</summary>
public sealed class McpTokenUseTrackerTests
{
    private const string Token = "0123456789abcdef";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class RecordingStore : IMcpTokenUseStore
    {
        public List<(string Id, DateTimeOffset At)> Writes { get; } = [];

        public Func<Task>? Behavior { get; set; }

        public async Task UpsertAsync(string tokenId, DateTimeOffset lastUsedAt, CancellationToken ct)
        {
            if (Behavior is { } behavior)
            {
                await behavior().ConfigureAwait(false);
            }

            lock (Writes)
            {
                Writes.Add((tokenId, lastUsedAt));
            }
        }

        public Task<IReadOnlyDictionary<string, DateTimeOffset>> ListAsync(IReadOnlyCollection<string> tokenIds, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, DateTimeOffset>>(Writes.GroupBy(w => w.Id).ToDictionary(g => g.Key, g => g.Max(w => w.At)));
    }

    private static (McpTokenUseTracker Tracker, RecordingStore Store, FakeTimeProvider Time, LogCapture Logs) Create()
    {
        var store = new RecordingStore();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 5, 1, 9, 0, 0, TimeSpan.Zero));
        var logs = new LogCapture();
        return (new McpTokenUseTracker(store, time, LoggerFactory.Create(b => b.AddProvider(logs)).CreateLogger<McpTokenUseTracker>()), store, time, logs);
    }

    [Fact]
    public async Task One_write_per_token_per_five_minute_window()
    {
        var (tracker, store, time, _) = Create();

        await tracker.TouchAsync(Token, Ct);
        await tracker.TouchAsync(Token, Ct);
        time.Advance(TimeSpan.FromMinutes(4));
        await tracker.TouchAsync(Token, Ct);
        await tracker.FlushAsync(Ct);
        Assert.Single(store.Writes);

        time.Advance(TimeSpan.FromMinutes(2));
        await tracker.TouchAsync(Token, Ct);
        await tracker.FlushAsync(Ct);
        Assert.Equal(2, store.Writes.Count);
        await tracker.TouchAsync("fedcba9876543210", Ct);
        await tracker.FlushAsync(Ct);
        Assert.Equal(3, store.Writes.Count);
    }

    [Fact]
    public async Task A_failed_touch_is_retried_by_the_next_request_and_logs_the_exception_type_only()
    {
        var (tracker, store, _, logs) = Create();
        store.Behavior = () => throw new InvalidOperationException("secret-detail-from-the-database");

        await tracker.TouchAsync(Token, Ct); // never throws
        await tracker.FlushAsync(Ct);
        Assert.Empty(store.Writes);

        store.Behavior = null;
        await tracker.TouchAsync(Token, Ct);
        await tracker.FlushAsync(Ct);
        Assert.Single(store.Writes);
        var line = Assert.Single(logs.Entries);
        Assert.Contains("InvalidOperationException", line, StringComparison.Ordinal);
        Assert.Contains(Token, line, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-detail-from-the-database", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_touches_for_one_token_share_one_in_flight_upsert()
    {
        var (tracker, store, _, _) = Create();
        var gate = new TaskCompletionSource();
        store.Behavior = () => gate.Task;

        var first = tracker.TouchAsync(Token, Ct);
        var second = tracker.TouchAsync(Token, Ct);
        Assert.False(first.IsCompleted);
        gate.SetResult();
        await Task.WhenAll(first, second);
        await tracker.FlushAsync(Ct);

        Assert.Single(store.Writes);
    }

    [Fact]
    public async Task A_never_completing_store_does_not_delay_the_caller_and_is_cancelled_after_two_seconds()
    {
        var (tracker, store, time, logs) = Create();
        store.Behavior = () => new TaskCompletionSource().Task; // ignores every token

        var touch = tracker.TouchAsync(Token, Ct);
        var flush = tracker.FlushAsync(Ct);
        Assert.False(flush.IsCompleted);

        time.Advance(McpTokenUseTracker.Timeout - TimeSpan.FromMilliseconds(1));
        Assert.False(flush.IsCompleted);
        time.Advance(TimeSpan.FromMilliseconds(1));
        await flush;
        await touch;

        Assert.Empty(store.Writes);
        Assert.Contains("CanceledException", Assert.Single(logs.Entries), StringComparison.Ordinal);
        // the failed touch is retried by the next request
        store.Behavior = null;
        await tracker.TouchAsync(Token, Ct);
        await tracker.FlushAsync(Ct);
        Assert.Single(store.Writes);
    }

    [Fact]
    public async Task Forget_drops_the_throttle_entry_so_a_later_touch_writes_again()
    {
        var (tracker, store, _, _) = Create();
        await tracker.TouchAsync(Token, Ct);
        await tracker.FlushAsync(Ct);

        tracker.Forget(Token);
        await tracker.TouchAsync(Token, Ct);
        await tracker.FlushAsync(Ct);

        Assert.Equal(2, store.Writes.Count);
    }

    [Fact]
    public async Task The_caller_token_only_abandons_waiting_and_never_cancels_the_upsert()
    {
        var (tracker, store, _, _) = Create();
        var gate = new TaskCompletionSource();
        store.Behavior = () => gate.Task;
        using var requestAborted = new CancellationTokenSource();

        var touch = tracker.TouchAsync(Token, requestAborted.Token);
        await requestAborted.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => touch);
        gate.SetResult();
        await tracker.FlushAsync(Ct);

        Assert.Single(store.Writes);
    }
}
