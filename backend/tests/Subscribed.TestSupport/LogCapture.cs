using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Subscribed.TestSupport;

/// <summary>
/// Captures every log entry that passes the host's filters, rendered with message, all structured
/// values and exception text, so tests can prove that forbidden values never reach a log.
/// </summary>
public sealed class LogCapture : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    public IReadOnlyList<string> Entries => _entries.ToArray();

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, _entries);

    public void Dispose()
    {
    }

    public IReadOnlyList<string> Find(params IEnumerable<string> forbidden)
    {
        var values = forbidden.Where(v => !string.IsNullOrEmpty(v) && v.Length >= 6).Distinct().ToList();
        return Entries.Where(e => values.Any(v => e.Contains(v, StringComparison.Ordinal))).ToList();
    }

    private sealed class CaptureLogger(string category, ConcurrentQueue<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var sb = new StringBuilder();
            sb.Append(logLevel).Append(' ').Append(category).Append(' ').Append(formatter(state, exception));
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var (key, value) in values)
                {
                    sb.Append(" |").Append(key).Append('=').Append(value);
                }
            }

            if (exception is not null)
            {
                sb.Append(" |exception=").Append(exception);
            }

            entries.Enqueue(sb.ToString());
        }
    }
}
