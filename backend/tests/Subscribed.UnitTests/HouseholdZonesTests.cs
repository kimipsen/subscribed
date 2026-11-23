using Microsoft.Extensions.Logging;
using Subscribed.Household;
using Subscribed.Subscriptions;
using Xunit;

namespace Subscribed.UnitTests;

public sealed class HouseholdZonesTests
{
    private sealed class Capture : ILoggerProvider, ILogger
    {
        public List<string> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add(formatter(state, exception));

        public void Dispose()
        {
        }
    }

    [Fact]
    public void A_resolvable_zone_is_returned_without_a_warning()
    {
        var logs = new Capture();

        var zone = HouseholdZones.Resolve(new HouseholdView { TimeZoneId = "UTC" }, LoggerFactory.Create(b => b.AddProvider(logs)));

        Assert.Equal(TimeZoneInfo.Utc, zone);
        Assert.Empty(logs.Entries);
    }

    [Fact]
    public void An_unresolvable_zone_falls_back_to_utc_with_one_warning_that_does_not_echo_the_zone_id()
    {
        var logs = new Capture();

        var zone = HouseholdZones.Resolve(new HouseholdView { TimeZoneId = "Mars/Olympus-Mons" }, LoggerFactory.Create(b => b.AddProvider(logs)));

        Assert.Equal(TimeZoneInfo.Utc, zone);
        var line = Assert.Single(logs.Entries);
        Assert.DoesNotContain("Olympus", line, StringComparison.Ordinal);
    }
}
