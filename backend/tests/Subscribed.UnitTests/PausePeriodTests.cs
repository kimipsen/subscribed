using System.Globalization;
using System.Text.Json;
using Subscribed.Subscriptions.Domain;
using Xunit;

namespace Subscribed.UnitTests;

public sealed class PausePeriodIdTests
{
    [Theory]
    [InlineData("legacy-1", true)]
    [InlineData("legacy-0", true)]
    [InlineData("legacy-9999", true)]
    [InlineData("legacy-00001", false)]
    [InlineData("legacy-12345", false)]
    [InlineData("legacy-", false)]
    [InlineData("legacy-1a", false)]
    [InlineData("legacy--1", false)]
    [InlineData("Legacy-1", false)]
    [InlineData("0198a3b2c4d57e6f8091a2b3c4d5e6f7", true)]
    [InlineData("0198A3B2C4D57E6F8091A2B3C4D5E6F7", false)]
    [InlineData("0198a3b2c4d57e6f8091a2b3c4d5e6f", false)]
    [InlineData("0198a3b2c4d57e6f8091a2b3c4d5e6f77", false)]
    [InlineData(" 0198a3b2c4d57e6f8091a2b3c4d5e6f7", false)]
    [InlineData("0198a3b2c4d57e6f8091a2b3c4d5e6f7 ", false)]
    [InlineData(" legacy-1", false)]
    [InlineData("legacy-1 ", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TryParse_accepts_only_the_two_id_shapes(string? value, bool expected)
    {
        Assert.Equal(expected, PausePeriodIds.TryParse(value, out var id));
        Assert.Equal(expected ? value : "", id);
    }
}

public sealed class PausePeriodStateTests
{
    private static readonly DateTimeOffset Now = SubscriptionDecisionTests.Now;
    private static readonly DateOnly Today = SubscriptionDecisionTests.Today;

    private static List<object> Sub(string start = "2024-01-01") =>
        SubscriptionDecisionTests.Added(SubscriptionDecisionTests.Input(start: start, amount: "10.00"));

    private static DateTimeOffset At(string iso) => DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture);

    private static DateOnly D(string text) => DateOnly.Parse(text, CultureInfo.InvariantCulture);

    private static PausePeriod Single(List<object> history, TimeZoneInfo zone, DateOnly? today = null) =>
        Assert.Single(SubscriptionState.Rehydrate(history).ResolvePausePeriods(zone, today ?? Today));

    [Fact]
    public void A_legacy_pair_becomes_a_period_with_a_legacy_id_in_the_household_zone()
    {
        var history = Sub().WithPaused(At("2026-03-10T10:00:00Z")).WithResumed(At("2026-03-20T10:00:00Z"));
        var period = Single(history, TestZones.Copenhagen);
        Assert.Equal(new PausePeriod("legacy-1", D("2026-03-10"), D("2026-03-20")), period);
    }

    [Fact]
    public void A_legacy_instant_is_converted_to_the_household_date_not_the_utc_date()
    {
        var history = Sub().WithPaused(At("2026-03-10T23:30:00Z")).WithResumed(At("2026-03-20T22:30:00Z"));
        Assert.Equal(new PausePeriod("legacy-1", D("2026-03-10"), D("2026-03-20")), Single(history, TestZones.Utc));
        Assert.Equal(new PausePeriod("legacy-1", D("2026-03-11"), D("2026-03-20")), Single(history, TestZones.Copenhagen));
        Assert.Equal(new PausePeriod("legacy-1", D("2026-03-11"), D("2026-03-21")), Single(history, TestZones.Auckland));
    }

    [Fact]
    public void An_open_legacy_pause_stays_open()
    {
        var history = Sub().WithPaused(At("2026-03-10T10:00:00Z"));
        Assert.Equal(new PausePeriod("legacy-1", D("2026-03-10"), null), Single(history, TestZones.Copenhagen));
    }

    [Fact]
    public void D11_a_legacy_pause_recorded_late_utc_is_clamped_to_today_for_a_household_ahead_of_utc()
    {
        // 23:30Z on the 13th is already the 14th in Copenhagen: without the clamp the subscription would not be paused today.
        var history = Sub().WithPaused(At("2026-11-13T23:30:00Z"));
        var state = SubscriptionState.Rehydrate(history);
        var periods = state.ResolvePausePeriods(TestZones.Copenhagen, Today);

        Assert.Equal(new PausePeriod("legacy-1", Today, null), Assert.Single(periods));
        Assert.True(SubscriptionCalculator.IsPausedOn(periods, Today));
        Assert.Equal(SubscriptionStatus.Paused, SubscriptionCalculator.StatusOn(state, Today, TestZones.Copenhagen));
    }

    [Fact]
    public void D11_resume_succeeds_and_removes_the_clamped_period_instead_of_throwing()
    {
        var history = Sub().WithPaused(At("2026-11-13T23:30:00Z"));
        var events = SubscriptionDecisions.Resume(history, Today, TestZones.Copenhagen, Now);
        var removed = Assert.IsType<SubscriptionPausePeriodRemoved>(Assert.Single(events));
        Assert.Equal("legacy-1", removed.PeriodId);
        history.AddRange(events);
        Assert.Empty(SubscriptionState.Rehydrate(history).ResolvePausePeriods(TestZones.Copenhagen, Today));
    }

    [Fact]
    public void D11_editing_the_clamped_period_passes_the_future_date_check()
    {
        var history = Sub().WithPaused(At("2026-11-13T23:30:00Z"));
        var events = SubscriptionDecisions.ChangePausePeriod(history, "legacy-1", new PauseChange(D("2026-11-12"), null), Today, TestZones.Copenhagen, Now);
        var changed = Assert.IsType<SubscriptionPausePeriodChanged>(Assert.Single(events));
        Assert.Equal(D("2026-11-12"), changed.PauseDate);

        // Saving the unchanged (clamped) dates is also fine and emits nothing.
        Assert.Empty(SubscriptionDecisions.ChangePausePeriod(history, "legacy-1", new PauseChange(Today, null), Today, TestZones.Copenhagen, Now));
    }

    [Fact]
    public void D11_a_pair_that_clamps_to_the_same_day_is_dropped_as_zero_day()
    {
        var history = Sub().WithPaused(At("2026-11-13T23:10:00Z")).WithResumed(At("2026-11-13T23:40:00Z"));
        Assert.Empty(SubscriptionState.Rehydrate(history).ResolvePausePeriods(TestZones.Copenhagen, Today));
    }

    [Fact]
    public void D11_the_clamp_follows_today_so_the_date_does_not_drift()
    {
        var history = Sub().WithPaused(At("2026-11-13T23:30:00Z"));
        var state = SubscriptionState.Rehydrate(history);
        Assert.Equal(Today, Assert.Single(state.ResolvePausePeriods(TestZones.Copenhagen, Today)).PauseDate);
        Assert.Equal(D("2026-11-14"), Assert.Single(state.ResolvePausePeriods(TestZones.Copenhagen, D("2026-11-14"))).PauseDate);
        Assert.Equal(D("2026-11-14"), Assert.Single(state.ResolvePausePeriods(TestZones.Copenhagen, D("2026-11-20"))).PauseDate);
    }

    [Fact]
    public void D11_the_dst_transition_day_converts_with_the_zone_offset_of_that_instant()
    {
        // Copenhagen leaves summer time on 2026-10-25 at 01:00Z; 23:30Z on the 24th is 01:30 on the 25th local (CEST, UTC+2).
        var history = Sub().WithPaused(At("2026-10-24T23:30:00Z")).WithResumed(At("2026-10-25T23:30:00Z"));
        Assert.Equal(new PausePeriod("legacy-1", D("2026-10-25"), D("2026-10-26")), Single(history, TestZones.Copenhagen, D("2026-12-01")));
    }

    [Fact]
    public void Explicit_dates_are_never_clamped()
    {
        var state = SubscriptionState.Rehydrate(Sub());
        state.PausePeriods.Add(new PausePeriodState { PeriodId = "x", PauseDate = D("2026-12-01"), ResumeDate = D("2026-12-10") });
        Assert.Equal(new PausePeriod("x", D("2026-12-01"), D("2026-12-10")), Assert.Single(state.ResolvePausePeriods(TestZones.Copenhagen, Today)));
    }

    [Fact]
    public void D7_a_zero_day_legacy_pair_is_omitted()
    {
        var history = Sub().WithPaused(At("2026-03-10T08:00:00Z")).WithResumed(At("2026-03-10T09:00:00Z"));
        Assert.Empty(SubscriptionState.Rehydrate(history).ResolvePausePeriods(TestZones.Copenhagen, Today));
        Assert.Single(SubscriptionState.Rehydrate(history).PausePeriods);
    }

    [Fact]
    public void Periods_are_ordered_by_pause_date_then_id()
    {
        var state = SubscriptionState.Rehydrate(Sub());
        state.PausePeriods.Add(new PausePeriodState { PeriodId = "b", PauseDate = D("2026-02-01"), ResumeDate = D("2026-02-05") });
        state.PausePeriods.Add(new PausePeriodState { PeriodId = "z", PauseDate = D("2026-01-01"), ResumeDate = D("2026-01-05") });
        state.PausePeriods.Add(new PausePeriodState { PeriodId = "a", PauseDate = D("2026-02-01"), ResumeDate = D("2026-02-05") });
        Assert.Equal(["z", "a", "b"], state.ResolvePausePeriods(TestZones.Utc, Today).Select(p => p.PeriodId));
    }

    // ---- replay ----------------------------------------------------------------------------------

    [Fact]
    public void Replay_of_every_old_event_type_still_succeeds()
    {
        var history = Sub();
        history.Add(new SubscriptionDetailsChanged("Renamed", null, null, Now));
        history.WithPaused(Now).WithResumed(Now.AddDays(1));
        history.Add(new SubscriptionCancelByChanged(null, Now));
        history.Add(new SubscriptionCatalogLinked(null, Now));
        var state = SubscriptionState.Rehydrate(history);
        Assert.Equal("Renamed", state.Name);
        Assert.Equal(history.Count, state.Version);
        Assert.Equal(1, state.LegacyPauseCount);
    }

    [Fact]
    public void The_legacy_counter_is_never_decremented_so_an_id_is_never_reused()
    {
        var history = Sub().WithPaused(At("2026-02-01T10:00:00Z"));
        history.Add(new SubscriptionPausePeriodRemoved("legacy-1", Now));
        history.WithPaused(At("2026-03-01T10:00:00Z"));
        var state = SubscriptionState.Rehydrate(history);
        Assert.Equal("legacy-2", Assert.Single(state.PausePeriods).PeriodId);
        Assert.Equal(2, state.LegacyPauseCount);
    }

    [Fact]
    public void Changing_a_legacy_period_clears_its_instants_so_the_explicit_dates_win()
    {
        var history = Sub().WithPaused(At("2026-02-01T10:00:00Z")).WithResumed(At("2026-02-10T10:00:00Z"));
        history.Add(new SubscriptionPausePeriodChanged("legacy-1", D("2026-02-03"), D("2026-02-04"), Now));
        var period = Assert.Single(SubscriptionState.Rehydrate(history).PausePeriods);
        Assert.Null(period.PausedInstant);
        Assert.Null(period.ResumedInstant);
        Assert.Equal(new PausePeriod("legacy-1", D("2026-02-03"), D("2026-02-04")), Single(history, TestZones.Copenhagen));
    }

    [Fact]
    public void Unknown_period_ids_are_no_ops_on_replay()
    {
        var history = Sub();
        history.Add(new SubscriptionPausePeriodChanged("nope", D("2026-02-03"), null, Now));
        history.Add(new SubscriptionPausePeriodRemoved("nope", Now));
        var state = SubscriptionState.Rehydrate(history);
        Assert.Empty(state.PausePeriods);
        Assert.Equal(3, state.Version);
    }

    [Fact]
    public void An_orphan_legacy_resume_is_a_no_op()
    {
        var history = Sub().WithResumed(Now);
        var state = SubscriptionState.Rehydrate(history);
        Assert.Empty(state.PausePeriods);
        Assert.Equal(2, state.Version);
    }

    [Fact]
    public void A_changed_event_for_a_removed_period_rebuilds_the_same_state_as_without_it()
    {
        var baseline = Sub();
        baseline.Add(new SubscriptionPausePeriodAdded("p1", D("2026-02-01"), D("2026-02-05"), Now));
        baseline.Add(new SubscriptionPausePeriodRemoved("p1", Now));

        var withStray = baseline.ToList();
        withStray.Add(new SubscriptionPausePeriodChanged("p1", D("2026-03-01"), null, Now));

        var a = SubscriptionState.Rehydrate(baseline);
        var b = SubscriptionState.Rehydrate(withStray);
        Assert.Equal(JsonSerializer.Serialize(a.PausePeriods), JsonSerializer.Serialize(b.PausePeriods));
        Assert.Empty(b.PausePeriods);
    }

    [Fact]
    public void A_removed_event_followed_by_a_legacy_resume_does_not_close_the_wrong_period()
    {
        var history = Sub().WithPaused(At("2026-02-01T10:00:00Z"));
        history.Add(new SubscriptionPausePeriodAdded("explicit", D("2026-03-01"), null, Now));
        history.Add(new SubscriptionPausePeriodRemoved("legacy-1", Now));
        history.WithResumed(At("2026-03-10T10:00:00Z"));

        var period = Assert.Single(SubscriptionState.Rehydrate(history).PausePeriods);
        Assert.Equal("explicit", period.PeriodId);
        Assert.Null(period.ResumeDate);
        Assert.Null(period.ResumedInstant);
    }

    [Fact]
    public void A_legacy_resume_closes_the_last_open_legacy_period_only()
    {
        var history = Sub().WithPaused(At("2026-02-01T10:00:00Z")).WithResumed(At("2026-02-05T10:00:00Z")).WithPaused(At("2026-03-01T10:00:00Z"));
        history.Add(new SubscriptionPausePeriodAdded("explicit", D("2026-04-01"), null, Now));
        history.WithResumed(At("2026-03-10T10:00:00Z"));

        var periods = SubscriptionState.Rehydrate(history).ResolvePausePeriods(TestZones.Utc, Today);
        Assert.Equal(
            [new PausePeriod("legacy-1", D("2026-02-01"), D("2026-02-05")), new PausePeriod("legacy-2", D("2026-03-01"), D("2026-03-10")), new PausePeriod("explicit", D("2026-04-01"), null)],
            periods);
    }

    [Fact]
    public void Pause_events_before_the_subscription_was_added_are_rejected()
    {
        Assert.Throws<InvalidOperationException>(() => SubscriptionState.Rehydrate([new SubscriptionPaused(Now)]));
        Assert.Throws<InvalidOperationException>(() => SubscriptionState.Rehydrate([new SubscriptionResumed(Now)]));
        Assert.Throws<InvalidOperationException>(() => SubscriptionState.Rehydrate([new SubscriptionPausePeriodAdded("p", Today, null, Now)]));
    }

    [Fact]
    public void A_snapshot_stored_before_pause_periods_deserializes_as_not_paused()
    {
        var state = SubscriptionState.Rehydrate(Sub());
        state.Id = "abc";
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(state, SubscriptionEventTypes.Json))!.AsObject();
        Assert.True(node.Remove("pausePeriods"));
        Assert.True(node.Remove("legacyPauseCount"));
        node["paused"] = true;
        node["pausedAt"] = "2026-03-10T10:00:00+00:00";

        var old = JsonSerializer.Deserialize<SubscriptionState>(node.ToJsonString(), SubscriptionEventTypes.Json)!;
        Assert.Empty(old.PausePeriods);
        Assert.Equal(0, old.LegacyPauseCount);
        Assert.Equal(SubscriptionStatus.Active, SubscriptionCalculator.StatusOn(old, Today, TestZones.Utc));
    }

    [Fact]
    public void The_state_has_no_floating_point_members_and_no_paused_flag()
    {
        foreach (var type in new[] { typeof(SubscriptionState), typeof(PausePeriodState) })
        {
            Assert.DoesNotContain(type.GetProperties(), p => p.PropertyType == typeof(double) || p.PropertyType == typeof(float));
        }

        Assert.DoesNotContain(typeof(SubscriptionState).GetProperties(), p => p.Name is "Paused" or "PausedAt");
    }
}

public sealed class PauseDecisionTests
{
    private static readonly DateTimeOffset Now = SubscriptionDecisionTests.Now;
    private static readonly DateOnly Today = SubscriptionDecisionTests.Today;
    private static readonly TimeZoneInfo Zone = TestZones.Copenhagen;

    private static DateOnly D(string text) => DateOnly.Parse(text, CultureInfo.InvariantCulture);

    private static List<object> Sub(string start = "2024-01-01", string? end = null) =>
        SubscriptionDecisionTests.Added(SubscriptionDecisionTests.Input(start: start, end: end, amount: "10.00"));

    /// <summary>A history with explicit periods written straight in, so the test cannot depend on the decisions under test.</summary>
    private static List<object> WithPeriods(params (string Id, string From, string? To)[] periods)
    {
        var history = Sub();
        foreach (var (id, from, to) in periods)
        {
            history.Add(new SubscriptionPausePeriodAdded(id, D(from), to is null ? null : D(to), Now));
        }

        return history;
    }

    private static PauseChange Change(string from, string? to = null) => new(D(from), to is null ? null : D(to));

    private static List<string> Keys(Action action) => [.. Assert.Throws<SubscriptionValidationException>(action).Errors.Keys.Order(StringComparer.Ordinal)];

    private static IReadOnlyList<object> Add(List<object> history, PauseChange change) =>
        SubscriptionDecisions.AddPausePeriod(history, change, "new", Today, Zone, Now);

    private static IReadOnlyList<object> Edit(List<object> history, string id, PauseChange change) =>
        SubscriptionDecisions.ChangePausePeriod(history, id, change, Today, Zone, Now);

    private static IReadOnlyList<PausePeriod> After(List<object> history, params object[] events)
    {
        history.AddRange(events);
        return SubscriptionState.Rehydrate(history).ResolvePausePeriods(Zone, Today);
    }

    // ---- buttons ---------------------------------------------------------------------------------

    [Fact]
    public void Pause_opens_a_period_that_starts_today()
    {
        var history = Sub();
        var added = Assert.IsType<SubscriptionPausePeriodAdded>(Assert.Single(SubscriptionDecisions.Pause(history, "id1", Today, Zone, Now.ToOffset(TimeSpan.FromHours(2)))));
        Assert.Equal("id1", added.PeriodId);
        Assert.Equal(Today, added.PauseDate);
        Assert.Null(added.ResumeDate);
        Assert.Equal(TimeSpan.Zero, added.RecordedAt.Offset);
    }

    [Fact]
    public void Pausing_a_paused_subscription_is_a_409_and_changes_nothing()
    {
        var history = WithPeriods(("p1", "2026-11-01", null));
        var before = history.ToList();
        Assert.Throws<SubscriptionStateException>(() => SubscriptionDecisions.Pause(history, "id2", Today, Zone, Now));
        Assert.Equal(before, history);
    }

    [Fact]
    public void Resume_closes_the_open_period_with_today_as_the_resume_date()
    {
        var history = WithPeriods(("p1", "2026-11-10", null));
        var changed = Assert.IsType<SubscriptionPausePeriodChanged>(Assert.Single(SubscriptionDecisions.Resume(history, Today, Zone, Now)));
        Assert.Equal(new SubscriptionPausePeriodChanged("p1", D("2026-11-10"), Today, Now), changed);
    }

    [Fact]
    public void D6_resume_on_the_pause_day_removes_the_period()
    {
        var history = WithPeriods(("p1", "2026-11-13", null));
        var removed = Assert.IsType<SubscriptionPausePeriodRemoved>(Assert.Single(SubscriptionDecisions.Resume(history, Today, Zone, Now)));
        Assert.Equal("p1", removed.PeriodId);
        Assert.Empty(After(history, removed));
    }

    [Fact]
    public void Resuming_when_not_paused_is_a_409_also_after_a_resume()
    {
        var history = Sub();
        Assert.Throws<SubscriptionStateException>(() => SubscriptionDecisions.Resume(history, Today, Zone, Now));

        var closed = WithPeriods(("p1", "2026-11-01", "2026-11-05"));
        Assert.Throws<SubscriptionStateException>(() => SubscriptionDecisions.Resume(closed, Today, Zone, Now));
    }

    [Fact]
    public void A_subscription_can_be_paused_again_after_it_was_resumed_and_periods_may_be_adjacent()
    {
        var history = WithPeriods(("p1", "2026-11-10", null));
        var resumed = SubscriptionDecisions.Resume(history, Today, Zone, Now);
        history.AddRange(resumed);
        var paused = SubscriptionDecisions.Pause(history, "p2", Today, Zone, Now);
        Assert.Equal(new SubscriptionPausePeriodAdded("p2", Today, null, Now), Assert.Single(paused));
        Assert.Equal(2, After(history, [.. paused]).Count);
    }

    [Fact]
    public void Pause_and_resume_on_a_removed_subscription_or_an_empty_history_are_not_found()
    {
        Assert.Throws<SubscriptionNotFoundException>(() => SubscriptionDecisions.Pause([], "id", Today, Zone, Now));
        Assert.Throws<SubscriptionNotFoundException>(() => SubscriptionDecisions.Resume([], Today, Zone, Now));
        Assert.Throws<SubscriptionNotFoundException>(() => SubscriptionDecisions.AddPausePeriod([], Change("2026-01-01"), "id", Today, Zone, Now));
        Assert.Throws<SubscriptionNotFoundException>(() => SubscriptionDecisions.RemovePausePeriod([], "id", Today, Zone, Now));

        var history = Sub();
        history.AddRange(SubscriptionDecisions.Remove(history, Now));
        Assert.Throws<SubscriptionNotFoundException>(() => SubscriptionDecisions.Pause(history, "id", Today, Zone, Now));
        Assert.Throws<SubscriptionNotFoundException>(() => SubscriptionDecisions.Resume(history, Today, Zone, Now));
    }

    [Fact]
    public void Pause_is_allowed_before_the_start_and_after_the_end()
    {
        Assert.Single(SubscriptionDecisions.Pause(Sub(start: "2030-01-01"), "id", Today, Zone, Now));
        Assert.Single(SubscriptionDecisions.Pause(Sub(end: "2024-12-31"), "id", Today, Zone, Now));
    }

    [Fact]
    public void A_paused_subscription_can_still_be_edited_and_removed()
    {
        var history = WithPeriods(("p1", "2026-11-10", null));
        history.AddRange(SubscriptionDecisions.UpdateDetails(history, new DetailsUpdate("Renamed", null, null, "DKK", new DateOnly(2024, 1, 1), null), Now));
        Assert.Equal("Renamed", SubscriptionState.Rehydrate(history).Name);
        history.AddRange(SubscriptionDecisions.Remove(history, Now));
        Assert.True(SubscriptionState.Rehydrate(history).Removed);
    }

    // ---- shape -----------------------------------------------------------------------------------

    [Fact]
    public void ParsePause_reads_the_two_dates()
    {
        Assert.Equal(new PauseChange(D("2026-02-01"), null), SubscriptionDecisions.ParsePause(new PauseInput("2026-02-01", null)));
        Assert.Equal(new PauseChange(D("2026-02-01"), D("2026-02-10")), SubscriptionDecisions.ParsePause(new PauseInput("2026-02-01", "2026-02-10")));
    }

    [Theory]
    [InlineData(null, null, "pauseDate")]
    [InlineData("", null, "pauseDate")]
    [InlineData("2026-2-1", null, "pauseDate")]
    [InlineData("01/02/2026", null, "pauseDate")]
    [InlineData("2026-02-01", "tomorrow", "resumeDate")]
    [InlineData("2026-02-01", "", "resumeDate")]
    [InlineData("2026-02-30", "2026-02-31", "pauseDate,resumeDate")]
    [InlineData(null, "bad", "pauseDate,resumeDate")]
    public void ParsePause_rejects_malformed_values_as_field_errors(string? pause, string? resume, string keys) =>
        Assert.Equal(keys.Split(','), Keys(() => SubscriptionDecisions.ParsePause(new PauseInput(pause, resume))));

    // ---- add -------------------------------------------------------------------------------------

    [Fact]
    public void Add_emits_one_event_for_a_valid_period()
    {
        var added = Assert.IsType<SubscriptionPausePeriodAdded>(Assert.Single(Add(Sub(), Change("2026-02-01", "2026-02-10"))));
        Assert.Equal(new SubscriptionPausePeriodAdded("new", D("2026-02-01"), D("2026-02-10"), Now), added);
    }

    [Theory]
    [InlineData("2026-11-14", null, "pauseDate")]
    [InlineData("2026-11-01", "2026-11-14", "resumeDate")]
    [InlineData("2026-11-14", "2026-11-20", "pauseDate,resumeDate")]
    [InlineData("2026-02-01", "2026-02-01", "resumeDate")]
    [InlineData("2026-02-10", "2026-02-01", "resumeDate")]
    public void Add_rejects_future_dates_and_a_resume_that_is_not_after_the_pause(string pause, string? resume, string keys) =>
        Assert.Equal(keys.Split(','), Keys(() => Add(Sub(), Change(pause, resume))));

    [Fact]
    public void Today_is_not_a_future_date()
    {
        Assert.Single(Add(Sub(), Change("2026-11-13")));
        Assert.Single(Add(Sub(), Change("2026-11-12", "2026-11-13")));
    }

    [Fact]
    public void Add_rejects_overlap_on_the_half_open_ranges()
    {
        var history = WithPeriods(("p1", "2026-03-01", "2026-03-10"));
        Assert.Equal(["pauseDate"], Keys(() => Add(history, Change("2026-03-05", "2026-03-08"))));
        Assert.Equal(["pauseDate"], Keys(() => Add(history, Change("2026-02-25", "2026-03-02"))));
        Assert.Equal(["pauseDate"], Keys(() => Add(history, Change("2026-03-09", "2026-03-20"))));
        Assert.Equal(["pauseDate"], Keys(() => Add(history, Change("2026-02-01", "2026-04-01"))));

        // Adjacent periods are allowed in both directions.
        Assert.Single(Add(history, Change("2026-03-10", "2026-03-15")));
        Assert.Single(Add(history, Change("2026-02-20", "2026-03-01")));
    }

    [Fact]
    public void Add_rejects_a_second_open_period_and_an_open_period_before_a_later_one()
    {
        var open = WithPeriods(("p1", "2026-11-01", null));
        Assert.Equal(["pauseDate", "resumeDate"], Keys(() => Add(open, Change("2026-11-05"))));

        var later = WithPeriods(("p1", "2026-03-05", "2026-03-08"));
        Assert.Equal(["pauseDate", "resumeDate"], Keys(() => Add(later, Change("2026-03-01"))));

        // An open period after every closed one is fine.
        Assert.Single(Add(later, Change("2026-03-08")));
        Assert.Single(Add(later, Change("2026-06-01")));
    }

    [Fact]
    public void Add_allows_a_closed_period_before_an_open_one()
    {
        var open = WithPeriods(("p1", "2026-11-10", null));
        Assert.Single(Add(open, Change("2026-11-01", "2026-11-05")));
        Assert.Single(Add(open, Change("2026-11-01", "2026-11-10")));
        Assert.Equal(["pauseDate"], Keys(() => Add(open, Change("2026-11-01", "2026-11-11"))));
    }

    [Fact]
    public void R5_6_periods_may_start_before_the_start_date_and_end_after_the_end_date()
    {
        var history = Sub(start: "2026-03-01", end: "2026-04-01");
        Assert.Single(Add(history, Change("2025-01-01", "2025-02-01")));
        Assert.Single(Add(history, Change("2026-05-01", "2026-06-01")));
        Assert.Single(Add(history, Change("2026-05-01")));
    }

    [Fact]
    public void Add_checks_the_overlap_between_legacy_and_explicit_periods()
    {
        var history = Sub().WithPaused(DateTimeOffset.Parse("2026-02-01T10:00:00Z", CultureInfo.InvariantCulture))
            .WithResumed(DateTimeOffset.Parse("2026-02-10T10:00:00Z", CultureInfo.InvariantCulture))
            .WithPaused(DateTimeOffset.Parse("2026-04-01T10:00:00Z", CultureInfo.InvariantCulture))
            .WithResumed(DateTimeOffset.Parse("2026-04-10T10:00:00Z", CultureInfo.InvariantCulture));

        Assert.Single(Add(history, Change("2026-03-01", "2026-03-10")));
        Assert.Equal(["pauseDate"], Keys(() => Add(history, Change("2026-02-05", "2026-03-10"))));
    }

    // ---- change ----------------------------------------------------------------------------------

    [Fact]
    public void Change_ignores_the_period_itself_in_the_overlap_check()
    {
        var history = WithPeriods(("p1", "2026-03-01", "2026-03-10"));
        var changed = Assert.IsType<SubscriptionPausePeriodChanged>(Assert.Single(Edit(history, "p1", Change("2026-03-02", "2026-03-12"))));
        Assert.Equal(new SubscriptionPausePeriodChanged("p1", D("2026-03-02"), D("2026-03-12"), Now), changed);
    }

    [Fact]
    public void Change_with_the_same_dates_emits_nothing()
    {
        var history = WithPeriods(("p1", "2026-03-01", "2026-03-10"));
        Assert.Empty(Edit(history, "p1", Change("2026-03-01", "2026-03-10")));
    }

    [Fact]
    public void Change_applies_the_same_validation_as_add()
    {
        var history = WithPeriods(("p1", "2026-03-01", "2026-03-10"), ("p2", "2026-04-01", "2026-04-10"));
        Assert.Equal(["pauseDate"], Keys(() => Edit(history, "p1", Change("2026-03-01", "2026-04-05"))));
        Assert.Equal(["resumeDate"], Keys(() => Edit(history, "p1", Change("2026-03-01", "2026-03-01"))));
        Assert.Equal(["pauseDate"], Keys(() => Edit(history, "p1", Change("2026-11-14", null))));
        Assert.Equal(["pauseDate", "resumeDate"], Keys(() => Edit(history, "p1", Change("2026-03-01"))));
        Assert.Single(Edit(history, "p2", Change("2026-04-01")));
    }

    [Fact]
    public void Changing_a_legacy_period_so_that_it_overlaps_its_neighbour_is_rejected()
    {
        var history = Sub().WithPaused(DateTimeOffset.Parse("2026-03-10T10:00:00Z", CultureInfo.InvariantCulture))
            .WithResumed(DateTimeOffset.Parse("2026-03-20T10:00:00Z", CultureInfo.InvariantCulture));
        history.Add(new SubscriptionPausePeriodAdded("p2", D("2026-04-01"), D("2026-04-10"), Now));

        Assert.Equal(["pauseDate"], Keys(() => Edit(history, "legacy-1", Change("2026-03-10", "2026-04-05"))));
        Assert.Single(Edit(history, "legacy-1", Change("2026-03-10", "2026-04-01")));
    }

    [Fact]
    public void Inserting_a_closed_period_between_two_legacy_periods_works()
    {
        var history = Sub().WithPaused(DateTimeOffset.Parse("2026-02-01T10:00:00Z", CultureInfo.InvariantCulture))
            .WithResumed(DateTimeOffset.Parse("2026-02-10T10:00:00Z", CultureInfo.InvariantCulture))
            .WithPaused(DateTimeOffset.Parse("2026-04-01T10:00:00Z", CultureInfo.InvariantCulture))
            .WithResumed(DateTimeOffset.Parse("2026-04-10T10:00:00Z", CultureInfo.InvariantCulture));
        var periods = After(history, [.. Add(history, Change("2026-03-01", "2026-03-10"))]);
        Assert.Equal(["legacy-1", "new", "legacy-2"], periods.Select(p => p.PeriodId));
    }

    [Fact]
    public void Change_and_remove_of_an_unknown_id_are_not_found()
    {
        var history = WithPeriods(("p1", "2026-03-01", "2026-03-10"));
        Assert.Throws<SubscriptionNotFoundException>(() => Edit(history, "nope", Change("2026-03-01", "2026-03-10")));
        Assert.Throws<SubscriptionNotFoundException>(() => SubscriptionDecisions.RemovePausePeriod(history, "nope", Today, Zone, Now));
        Assert.Throws<SubscriptionNotFoundException>(() => Edit(history, "legacy-0", Change("2026-03-01", "2026-03-10")));
    }

    [Fact]
    public void A_dropped_zero_day_legacy_period_is_not_found()
    {
        var history = Sub().WithPaused(DateTimeOffset.Parse("2026-03-10T08:00:00Z", CultureInfo.InvariantCulture))
            .WithResumed(DateTimeOffset.Parse("2026-03-10T09:00:00Z", CultureInfo.InvariantCulture));
        Assert.Throws<SubscriptionNotFoundException>(() => SubscriptionDecisions.RemovePausePeriod(history, "legacy-1", Today, Zone, Now));
    }

    // ---- remove ----------------------------------------------------------------------------------

    [Fact]
    public void Deleting_the_middle_of_three_periods_leaves_the_other_two()
    {
        var history = WithPeriods(("p1", "2026-01-01", "2026-01-10"), ("p2", "2026-02-01", "2026-02-10"), ("p3", "2026-03-01", "2026-03-10"));
        var removed = Assert.IsType<SubscriptionPausePeriodRemoved>(Assert.Single(SubscriptionDecisions.RemovePausePeriod(history, "p2", Today, Zone, Now)));
        Assert.Equal(["p1", "p3"], After(history, removed).Select(p => p.PeriodId));
    }

    [Fact]
    public void Removing_a_legacy_period_works_and_its_id_is_not_reused()
    {
        var history = Sub().WithPaused(DateTimeOffset.Parse("2026-03-10T10:00:00Z", CultureInfo.InvariantCulture));
        var removed = Assert.Single(SubscriptionDecisions.RemovePausePeriod(history, "legacy-1", Today, Zone, Now));
        history.Add(removed);
        history.WithPaused(DateTimeOffset.Parse("2026-05-10T10:00:00Z", CultureInfo.InvariantCulture));
        Assert.Equal("legacy-2", Assert.Single(SubscriptionState.Rehydrate(history).PausePeriods).PeriodId);
    }
}
