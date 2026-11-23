using Subscribed.Subscriptions.Domain;
using Xunit;

namespace Subscribed.UnitTests;

/// <summary>#55 WP3: the optional <c>via</c> marker on five events, its stamping and rule D10 (via is the latest stamped event's, never carried forward).</summary>
public sealed class ViaStampingTests
{
    private static readonly DateTimeOffset At = new(2026, 1, 5, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Day = new(2026, 11, 13);

    private static string Fixture(string folder, string alias) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", folder, alias + ".json")).Trim();

    private static SubscriptionAdded Added(string? via = null) =>
        new("household-default", "StreamCo", null, null, "DKK", new DateOnly(2024, 3, 1), null, "e1", 129m, "month", 1, At, Via: via);

    [Theory]
    [InlineData(SubscriptionEventTypes.Added)]
    [InlineData(SubscriptionEventTypes.PriceEntryAdded)]
    [InlineData(SubscriptionEventTypes.PriceEntryCorrected)]
    [InlineData(SubscriptionEventTypes.PausePeriodAdded)]
    [InlineData(SubscriptionEventTypes.PausePeriodChanged)]
    public void Stored_events_without_via_still_deserialize_with_null_via_and_the_fixture_with_via_reads_mcp(string alias)
    {
        var old = SubscriptionEventTypes.Deserialize(alias, Fixture(Path.Combine("events", "legacy"), alias));
        Assert.IsAssignableFrom<IViaStamped>(old);
        Assert.Null(old.GetType().GetProperty("Via")!.GetValue(old));

        var current = SubscriptionEventTypes.Deserialize(alias, Fixture("events", alias));
        Assert.Equal("mcp", current.GetType().GetProperty("Via")!.GetValue(current));
        Assert.Equal(alias, SubscriptionEventTypes.AliasOf(current.GetType()));
    }

    [Fact]
    public void Via_stays_out_of_the_other_events()
    {
        foreach (var type in new[]
        {
            typeof(SubscriptionDetailsChanged), typeof(SubscriptionCurrencyCorrected), typeof(SubscriptionPeriodChanged), typeof(SubscriptionPriceEntryRemoved),
            typeof(SubscriptionRemoved), typeof(SubscriptionPaused), typeof(SubscriptionResumed), typeof(SubscriptionCatalogLinked),
            typeof(SubscriptionPriceEntryPaymentChanged), typeof(SubscriptionCancelByChanged), typeof(SubscriptionPausePeriodRemoved),
        })
        {
            Assert.DoesNotContain(type.GetProperties(), p => p.Name == "Via");
            Assert.False(typeof(IViaStamped).IsAssignableFrom(type), type.Name);
        }
    }

    [Fact]
    public void WithVia_sets_and_clears_the_marker_and_keeps_every_other_field()
    {
        var stamped = (SubscriptionAdded)Added().WithVia("mcp");
        Assert.Equal("mcp", stamped.Via);
        Assert.Equal(Added() with { Via = "mcp" }, stamped);
        Assert.Null(((SubscriptionAdded)stamped.WithVia(null)).Via);
    }

    [Fact]
    public void The_initial_price_row_of_an_added_event_carries_its_via()
    {
        Assert.Equal("mcp", SubscriptionState.Rehydrate([Added("mcp")]).Entries.Single().Via);
        Assert.Null(SubscriptionState.Rehydrate([Added()]).Entries.Single().Via);
    }

    [Fact]
    public void D10_a_web_correction_of_an_mcp_price_entry_clears_its_via()
    {
        var web = new SubscriptionPriceEntryCorrected("e1", new DateOnly(2024, 3, 1), 140m, "month", 1, At);
        Assert.Null(SubscriptionState.Rehydrate([Added("mcp"), web]).Entries.Single().Via);
    }

    [Fact]
    public void D10_an_mcp_correction_of_a_web_price_entry_sets_its_via()
    {
        var mcp = new SubscriptionPriceEntryCorrected("e1", new DateOnly(2024, 3, 1), 140m, "month", 1, At, "mcp");
        Assert.Equal("mcp", SubscriptionState.Rehydrate([Added(), mcp]).Entries.Single().Via);
    }

    [Fact]
    public void D10_a_price_entry_added_over_mcp_shows_via_and_a_web_one_does_not()
    {
        var state = SubscriptionState.Rehydrate(
        [
            Added(),
            new SubscriptionPriceEntryAdded("e2", new DateOnly(2025, 1, 1), 139m, "month", 1, At, "mcp"),
            new SubscriptionPriceEntryAdded("e3", new DateOnly(2026, 1, 1), 149m, "month", 1, At),
        ]);
        Assert.Equal([null, "mcp", null], state.Entries.Select(e => e.Via).ToArray());
    }

    [Fact]
    public void D10_pause_period_via_follows_the_latest_stamped_event_in_both_directions()
    {
        var added = new SubscriptionPausePeriodAdded("p1", new DateOnly(2026, 1, 1), null, At);
        var mcpResume = new SubscriptionPausePeriodChanged("p1", new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 1), At, "mcp");
        var webChange = new SubscriptionPausePeriodChanged("p1", new DateOnly(2026, 1, 2), new DateOnly(2026, 2, 1), At);

        // MCP resume of a web-created period sets it.
        Assert.Equal("mcp", Resolved(Added(), added, mcpResume).Via);

        // A web edit of an MCP-created period clears it.
        var mcpAdded = added with { Via = "mcp" };
        Assert.Equal("mcp", Resolved(Added(), mcpAdded).Via);
        Assert.Null(Resolved(Added(), mcpAdded, webChange).Via);
    }

    [Fact]
    public void Legacy_paused_and_resumed_events_stay_header_only()
    {
        var state = SubscriptionState.Rehydrate([Added("mcp"), new SubscriptionPaused(At), new SubscriptionResumed(At)]);
        Assert.Null(Assert.Single(state.PausePeriods).Via);
    }

    [Fact]
    public void A_replay_is_deterministic_so_a_snapshot_rebuild_gives_the_same_via_values()
    {
        object[] history =
        [
            Added("mcp"),
            new SubscriptionPriceEntryAdded("e2", new DateOnly(2025, 1, 1), 139m, "month", 1, At),
            new SubscriptionPausePeriodAdded("p1", new DateOnly(2026, 1, 1), null, At, "mcp"),
        ];
        var first = SubscriptionState.Rehydrate(history);
        var second = SubscriptionState.Rehydrate(history);
        Assert.Equal(first.Entries.Select(e => e.Via), second.Entries.Select(e => e.Via));
        Assert.Equal(first.PausePeriods.Select(p => p.Via), second.PausePeriods.Select(p => p.Via));
    }

    private static PausePeriod Resolved(params object[] events) =>
        Assert.Single(SubscriptionState.Rehydrate(events).ResolvePausePeriods(TimeZoneInfo.Utc, Day));
}
