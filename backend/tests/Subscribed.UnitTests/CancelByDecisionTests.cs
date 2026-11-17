using System.Globalization;
using Subscribed.Subscriptions.Domain;
using Xunit;

namespace Subscribed.UnitTests;

/// <summary>
/// The cancel-by deadline decisions (issue #33, plan sections 2 D1 to D6 and 4.1). Dates are named past dates, so the tests do not
/// drift with the calendar. The tzdata rules they depend on: Europe/Copenhagen springs forward on 2026-03-29 (02:00 to 03:00, so 02:30
/// does not exist) and falls back on 2026-10-25 (03:00 to 02:00, so 02:30 happens twice); Pacific/Auckland is on +13 (NZDT) in December.
/// </summary>
public sealed class CancelByDecisionTests
{
    private static readonly DateTimeOffset Now = SubscriptionDecisionTests.Now;

    private static DateTimeOffset Utc(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture).ToUniversalTime();

    private static AddInput Input(string? cancelBy, string start = "2024-03-01", string? end = null) =>
        SubscriptionDecisionTests.Input(start: start, end: end) with { CancelBy = cancelBy };

    private static List<object> AddWith(string? cancelBy, TimeZoneInfo zone, string start = "2024-03-01", string? end = null) =>
        [.. SubscriptionDecisions.Add(SubscriptionDecisions.ParseAdd(Input(cancelBy, start, end), zone), "household-default", "e1", Now)];

    private static SubscriptionValidationException Rejected(Action action) => Assert.Throws<SubscriptionValidationException>(action);

    private static string Key(Action action) => Assert.Single(Rejected(action).Errors).Key;

    /// <summary>A history for a subscription starting on <paramref name="start"/> whose stored deadline is <paramref name="instant"/>.</summary>
    private static List<object> HistoryWith(DateTimeOffset? instant, string start = "2024-03-01")
    {
        var history = SubscriptionDecisionTests.Added(SubscriptionDecisionTests.Input(start: start));
        if (instant is not null)
        {
            history.Add(new SubscriptionCancelByChanged(instant, Now));
        }

        return history;
    }

    /// <summary>The form as the client would resubmit it: every field from the current state, with the given overrides.</summary>
    private static DetailsUpdate Resubmit(List<object> history, TimeZoneInfo zone, string? cancelBy, string? name = null, string? start = null)
    {
        var s = SubscriptionState.Rehydrate(history);
        return SubscriptionDecisions.ParseUpdate(new UpdateInput(name ?? s.Name, s.Category, s.Notes, s.Currency,
            start ?? s.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            s.EndDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), s.CatalogId, s.UnsubscribeUrl, CancelBy: cancelBy), zone);
    }

    private static IReadOnlyList<object> Update(List<object> history, TimeZoneInfo zone, string? cancelBy, string? name = null, string? start = null) =>
        SubscriptionDecisions.UpdateDetails(history, Resubmit(history, zone, cancelBy, name, start), Now);

    // ---- add ---------------------------------------------------------------------------------------

    [Fact]
    public void Add_without_cancel_by_emits_no_cancel_by_event()
    {
        var events = AddWith(null, TestZones.Copenhagen);

        Assert.IsType<SubscriptionAdded>(Assert.Single(events));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Add_with_empty_cancel_by_means_none(string raw) => Assert.IsType<SubscriptionAdded>(Assert.Single(AddWith(raw, TestZones.Copenhagen)));

    [Fact]
    public void Add_with_cancel_by_emits_the_event_after_SubscriptionAdded_as_a_utc_instant()
    {
        var events = AddWith("2026-12-01T09:00", TestZones.Copenhagen);

        Assert.IsType<SubscriptionAdded>(events[0]);
        var changed = Assert.IsType<SubscriptionCancelByChanged>(events[1]);
        Assert.Equal(2, events.Count);
        Assert.Equal(Utc("2026-12-01T08:00:00Z"), changed.CancelBy);
        Assert.Equal(TimeSpan.Zero, changed.CancelBy!.Value.Offset);
        Assert.Equal(Now, changed.RecordedAt);
        Assert.Equal(Utc("2026-12-01T08:00:00Z"), SubscriptionState.Rehydrate(events).CancelBy);
    }

    [Fact]
    public void Add_cancel_by_event_comes_before_the_catalog_link_and_the_payment_option_assignment()
    {
        var input = Input("2026-12-01T09:00") with { CatalogId = "streamco", PaymentOptionId = "0192f3c1a1b27c4e8d2e5f60718293b5" };

        var events = SubscriptionDecisions.Add(SubscriptionDecisions.ParseAdd(input, TestZones.Copenhagen), "household-default", "e1", Now);

        var cancelBy = events.Select((e, i) => (e, i)).Single(x => x.e is SubscriptionCancelByChanged).i;
        var catalog = events.Select((e, i) => (e, i)).Single(x => x.e is SubscriptionCatalogLinked).i;
        Assert.Equal(1, cancelBy);
        Assert.True(cancelBy < catalog);
        Assert.IsType<SubscriptionAdded>(events[0]);
    }

    [Fact]
    public void Add_accepts_seconds_and_keeps_them()
    {
        var events = AddWith("2026-12-01T09:00:30", TestZones.Utc);

        Assert.Equal(Utc("2026-12-01T09:00:30Z"), Assert.IsType<SubscriptionCancelByChanged>(events[1]).CancelBy);
    }

    [Theory]
    [InlineData("2026-12-01T09:00Z")]
    [InlineData("2026-12-01T09:00:00Z")]
    [InlineData("2026-12-01T09:00+01:00")]
    [InlineData("2026-12-01T09:00:00-05:00")]
    [InlineData("2026-12-01 09:00")]
    [InlineData("2026-12-01")]
    [InlineData("2026-12-01T9:00")]
    [InlineData("2026-12-01T09:00:00.5")]
    [InlineData("2026-12-01T24:00")]
    [InlineData("2026-13-01T09:00")]
    [InlineData("01/12/2026 09:00")]
    [InlineData("tomorrow")]
    public void Add_rejects_a_wrong_format_or_an_offset_or_Z_on_cancel_by(string raw) =>
        Assert.Equal("cancelBy", Key(() => SubscriptionDecisions.ParseAdd(Input(raw), TestZones.Copenhagen)));

    [Fact]
    public void Add_rejects_a_local_time_in_the_dst_gap()
    {
        var errors = Rejected(() => SubscriptionDecisions.ParseAdd(Input("2026-03-29T02:30"), TestZones.Copenhagen)).Errors;

        Assert.Equal(SubscriptionText.CancelByGap, Assert.Single(Assert.Single(errors).Value));
        Assert.Equal("cancelBy", Assert.Single(errors).Key);
    }

    [Fact]
    public void Add_accepts_the_minutes_either_side_of_the_dst_gap()
    {
        Assert.Equal(Utc("2026-03-29T00:59:00Z"), Assert.IsType<SubscriptionCancelByChanged>(AddWith("2026-03-29T01:59", TestZones.Copenhagen)[1]).CancelBy);
        Assert.Equal(Utc("2026-03-29T01:00:00Z"), Assert.IsType<SubscriptionCancelByChanged>(AddWith("2026-03-29T03:00", TestZones.Copenhagen)[1]).CancelBy);
    }

    [Fact]
    public void Add_resolves_a_local_time_in_the_dst_overlap_to_the_earlier_instant()
    {
        var events = AddWith("2026-10-25T02:30", TestZones.Copenhagen);

        // 02:30 happens at 00:30Z (CEST, +02:00) and again at 01:30Z (CET, +01:00). The earlier one is the safe side for a reminder.
        Assert.Equal(Utc("2026-10-25T00:30:00Z"), Assert.IsType<SubscriptionCancelByChanged>(events[1]).CancelBy);
    }

    [Fact]
    public void Add_rejects_a_cancel_by_before_the_start_date_in_the_household_zone()
    {
        var errors = Rejected(() => SubscriptionDecisions.Add(SubscriptionDecisions.ParseAdd(Input("2026-11-30T23:59", "2026-12-01"), TestZones.Copenhagen), "h", "e1", Now)).Errors;

        Assert.Equal(SubscriptionText.CancelByBeforeStart, Assert.Single(Assert.Single(errors).Value));
        Assert.Equal("cancelBy", Assert.Single(errors).Key);
    }

    [Fact]
    public void Add_accepts_a_cancel_by_on_the_start_date_even_at_midnight()
    {
        var events = AddWith("2026-12-01T00:00", TestZones.Copenhagen, "2026-12-01");

        Assert.Equal(Utc("2026-11-30T23:00:00Z"), Assert.IsType<SubscriptionCancelByChanged>(events[1]).CancelBy);
    }

    [Fact]
    public void Add_checks_the_date_in_NewSubscription_Zone_across_the_date_line()
    {
        // 00:30 on 1 December in Auckland (+13) is 11:30Z on 30 November: the UTC date is before the start date, the local date is not.
        var accepted = AddWith("2026-12-01T00:30", TestZones.Auckland, "2026-12-01");
        Assert.Equal(Utc("2026-11-30T11:30:00Z"), Assert.IsType<SubscriptionCancelByChanged>(accepted[1]).CancelBy);

        Assert.Equal("cancelBy", Key(() => SubscriptionDecisions.Add(SubscriptionDecisions.ParseAdd(Input("2026-11-30T23:30", "2026-12-01"), TestZones.Auckland), "h", "e1", Now)));
    }

    [Fact]
    public void Add_does_not_compare_the_deadline_with_now_or_with_the_end_date()
    {
        // Now is 2026-11-13T12:00Z, which is 13:00 in Copenhagen. Inside 24 h, equal to now, one second after now and already past:
        // all stored as given. The feed decides what is still eligible (work item 6), not the decision.
        foreach (var (local, expected) in new[]
                 {
                     ("2026-11-14T09:00", "2026-11-14T08:00:00Z"),
                     ("2026-11-13T13:00", "2026-11-13T12:00:00Z"),
                     ("2026-11-13T13:00:01", "2026-11-13T12:00:01Z"),
                     ("2026-11-13T12:59:59", "2026-11-13T11:59:59Z"),
                     ("2026-11-01T09:00", "2026-11-01T08:00:00Z"),
                 })
        {
            Assert.Equal(Utc(expected), Assert.IsType<SubscriptionCancelByChanged>(AddWith(local, TestZones.Copenhagen)[1]).CancelBy);
        }

        // A deadline after EndDate is stored too (the feed behaviour for it is pinned with the feed).
        var late = AddWith("2027-02-01T09:00", TestZones.Copenhagen, end: "2026-12-31");
        Assert.Equal(Utc("2027-02-01T08:00:00Z"), Assert.IsType<SubscriptionCancelByChanged>(late[1]).CancelBy);
    }

    [Fact]
    public void Parse_requires_the_zone()
    {
        Assert.Throws<ArgumentNullException>(() => SubscriptionDecisions.ParseAdd(Input(null), null!));
        Assert.Throws<ArgumentNullException>(() => SubscriptionDecisions.ParseUpdate(new UpdateInput("n", null, null, "DKK", "2024-03-01", null), null!));
    }

    [Fact]
    public void Parse_collects_the_cancel_by_error_with_the_other_field_errors()
    {
        var errors = Rejected(() => SubscriptionDecisions.ParseAdd(Input("nope") with { Name = "" }, TestZones.Utc)).Errors;

        Assert.Contains("name", errors.Keys);
        Assert.Contains("cancelBy", errors.Keys);
    }

    // ---- parse: the local rendering ----------------------------------------------------------------

    [Fact]
    public void ParseUpdate_normalises_the_local_string_and_keeps_seconds_only_when_they_are_not_zero()
    {
        var history = HistoryWith(null);

        Assert.Equal("2026-12-01T09:00", Resubmit(history, TestZones.Utc, "2026-12-01T09:00:00").CancelByLocal);
        Assert.Equal("2026-12-01T09:00", Resubmit(history, TestZones.Utc, " 2026-12-01T09:00 ").CancelByLocal);
        Assert.Equal("2026-12-01T09:00:30", Resubmit(history, TestZones.Utc, "2026-12-01T09:00:30").CancelByLocal);
        Assert.Null(Resubmit(history, TestZones.Utc, null).CancelByLocal);
        Assert.Null(Resubmit(history, TestZones.Utc, "").CancelByLocal);
        Assert.Equal(TestZones.Utc, Resubmit(history, TestZones.Utc, null).Zone);
    }

    [Fact]
    public void RenderCancelBy_shows_the_stored_instant_in_the_zone()
    {
        Assert.Null(SubscriptionDecisions.RenderCancelBy(null, TestZones.Copenhagen));
        Assert.Equal("2026-12-01T09:00", SubscriptionDecisions.RenderCancelBy(Utc("2026-12-01T08:00:00Z"), TestZones.Copenhagen));
        Assert.Equal("2026-12-01T21:00", SubscriptionDecisions.RenderCancelBy(Utc("2026-12-01T08:00:00Z"), TestZones.Auckland));
        Assert.Equal("2026-12-01T08:00:30", SubscriptionDecisions.RenderCancelBy(Utc("2026-12-01T08:00:30Z"), null));
    }

    // ---- update ------------------------------------------------------------------------------------

    [Fact]
    public void Update_sets_a_cancel_by_where_there_was_none()
    {
        var history = HistoryWith(null);

        var changed = Assert.IsType<SubscriptionCancelByChanged>(Assert.Single(Update(history, TestZones.Copenhagen, "2026-12-01T09:00")));

        Assert.Equal(Utc("2026-12-01T08:00:00Z"), changed.CancelBy);
        Assert.Equal(Now, changed.RecordedAt);
    }

    [Fact]
    public void Update_changes_an_existing_cancel_by_without_a_details_event()
    {
        var history = HistoryWith(Utc("2026-12-01T08:00:00Z"));

        var changed = Assert.IsType<SubscriptionCancelByChanged>(Assert.Single(Update(history, TestZones.Copenhagen, "2026-12-02T10:15")));

        Assert.Equal(Utc("2026-12-02T09:15:00Z"), changed.CancelBy);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Update_clears_the_cancel_by_with_a_null_or_empty_value(string? raw)
    {
        var history = HistoryWith(Utc("2026-12-01T08:00:00Z"));

        var events = Update(history, TestZones.Copenhagen, raw);

        var changed = Assert.IsType<SubscriptionCancelByChanged>(Assert.Single(events));
        Assert.Null(changed.CancelBy);
        Assert.Null(SubscriptionState.Rehydrate([.. history, .. events]).CancelBy);
    }

    [Fact]
    public void Update_with_no_cancel_by_before_and_after_emits_nothing() => Assert.Empty(Update(HistoryWith(null), TestZones.Copenhagen, null));

    [Fact]
    public void Update_resubmitting_the_unchanged_local_string_emits_nothing()
    {
        var history = HistoryWith(Utc("2026-12-01T08:00:00Z"));

        Assert.Empty(Update(history, TestZones.Copenhagen, "2026-12-01T09:00"));
        Assert.Empty(Update(history, TestZones.Copenhagen, "2026-12-01T09:00:00"));
    }

    [Fact]
    public void Update_resubmitting_the_rendered_value_emits_nothing_when_the_deadline_has_seconds()
    {
        var history = HistoryWith(Utc("2026-12-01T08:00:30Z"));

        Assert.Empty(Update(history, TestZones.Copenhagen, SubscriptionDecisions.RenderCancelBy(Utc("2026-12-01T08:00:30Z"), TestZones.Copenhagen)));
        Assert.Single(Update(history, TestZones.Copenhagen, "2026-12-01T09:00"));
    }

    [Fact]
    public void Update_after_a_household_zone_change_resubmitting_the_new_local_rendering_emits_nothing()
    {
        // Stored 08:00Z was entered as 09:00 in Copenhagen. After the household moves to Auckland the form shows 21:00 and sends it back.
        var history = HistoryWith(Utc("2026-12-01T08:00:00Z"));
        var shown = SubscriptionDecisions.RenderCancelBy(Utc("2026-12-01T08:00:00Z"), TestZones.Auckland);

        Assert.Equal("2026-12-01T21:00", shown);
        Assert.Empty(Update(history, TestZones.Auckland, shown));
    }

    [Fact]
    public void Update_after_a_household_zone_change_submitting_the_old_wall_time_is_a_real_change()
    {
        var history = HistoryWith(Utc("2026-12-01T08:00:00Z"));

        var changed = Assert.IsType<SubscriptionCancelByChanged>(Assert.Single(Update(history, TestZones.Auckland, "2026-12-01T09:00")));

        Assert.Equal(Utc("2026-11-30T20:00:00Z"), changed.CancelBy);
    }

    [Theory]
    [InlineData("2026-10-25T00:30:00Z")] // the earlier instant: what Add stores for 02:30
    [InlineData("2026-10-25T01:30:00Z")] // the later instant: stored by an older client or a tz database change
    public void Update_in_a_dst_overlap_resubmitting_the_unchanged_local_string_never_moves_the_instant(string stored)
    {
        var history = HistoryWith(Utc(stored));

        Assert.Equal("2026-10-25T02:30", SubscriptionDecisions.RenderCancelBy(Utc(stored), TestZones.Copenhagen));
        Assert.Empty(Update(history, TestZones.Copenhagen, "2026-10-25T02:30"));
    }

    [Fact]
    public void Update_a_rename_after_a_zone_change_is_not_blocked_by_a_stale_cancel_by()
    {
        // Start 2026-12-01 and a deadline of 2026-12-01T00:30 Copenhagen (= 23:30Z on 30 November). In UTC that is the 30th, before the
        // start date, so the deadline is stale in the new zone. A rename that does not touch it must still save (D5 timing).
        var history = HistoryWith(Utc("2026-11-30T23:30:00Z"), "2026-12-01");
        var shown = SubscriptionDecisions.RenderCancelBy(Utc("2026-11-30T23:30:00Z"), TestZones.Utc);
        Assert.Equal("2026-11-30T23:30", shown);

        var events = Update(history, TestZones.Utc, shown, name: "Renamed");

        var details = Assert.IsType<SubscriptionDetailsChanged>(Assert.Single(events));
        Assert.Equal("Renamed", details.Name);
        Assert.Equal(Utc("2026-11-30T23:30:00Z"), SubscriptionState.Rehydrate([.. history, .. events]).CancelBy);
    }

    [Fact]
    public void Update_that_only_changes_cancel_by_to_before_the_start_date_is_rejected()
    {
        var history = HistoryWith(Utc("2026-12-05T08:00:00Z"), "2026-12-01");

        var errors = Rejected(() => Update(history, TestZones.Copenhagen, "2026-11-30T23:59")).Errors;

        Assert.Equal("cancelBy", Assert.Single(errors).Key);
        Assert.Equal(SubscriptionText.CancelByBeforeStart, Assert.Single(Assert.Single(errors).Value));
    }

    [Fact]
    public void Update_that_moves_the_start_date_past_an_unchanged_cancel_by_is_rejected()
    {
        var history = HistoryWith(Utc("2026-11-20T08:00:00Z"), "2026-11-01");

        Assert.Equal("cancelBy", Key(() => Update(history, TestZones.Copenhagen, "2026-11-20T09:00", start: "2026-12-01")));
    }

    [Fact]
    public void Update_that_moves_the_start_date_but_stays_before_the_cancel_by_emits_only_the_period_change()
    {
        var history = HistoryWith(Utc("2026-11-20T08:00:00Z"), "2026-11-01");

        var changed = Assert.IsType<SubscriptionPeriodChanged>(Assert.Single(Update(history, TestZones.Copenhagen, "2026-11-20T09:00", start: "2026-11-20")));

        Assert.Equal(new DateOnly(2026, 11, 20), changed.StartDate);
    }

    [Fact]
    public void Update_that_clears_the_cancel_by_while_moving_the_start_date_is_accepted()
    {
        var history = HistoryWith(Utc("2026-11-20T08:00:00Z"), "2026-11-01");

        var events = Update(history, TestZones.Copenhagen, null, start: "2026-12-01");

        Assert.Contains(events, e => e is SubscriptionCancelByChanged { CancelBy: null });
        Assert.Contains(events, e => e is SubscriptionPeriodChanged);
    }

    [Fact]
    public void Update_checks_the_start_date_rule_in_DetailsUpdate_Zone_across_the_date_line()
    {
        var history = HistoryWith(null, "2026-11-01");

        // 00:30 on 1 December in Auckland is still 30 November in UTC.
        var accepted = Assert.IsType<SubscriptionCancelByChanged>(Assert.Single(Update(history, TestZones.Auckland, "2026-12-01T00:30", start: "2026-12-01").OfType<SubscriptionCancelByChanged>()));
        Assert.Equal(Utc("2026-11-30T11:30:00Z"), accepted.CancelBy);

        Assert.Equal("cancelBy", Key(() => Update(history, TestZones.Auckland, "2026-11-30T23:30", start: "2026-12-01")));
    }

    [Theory]
    [InlineData("2026-12-01T09:00Z")]
    [InlineData("2026-12-01T09:00+01:00")]
    [InlineData("2026-12-01 09:00")]
    [InlineData("garbage")]
    public void Update_rejects_a_wrong_format_or_an_offset_or_Z_on_cancel_by(string raw) =>
        Assert.Equal("cancelBy", Key(() => Resubmit(HistoryWith(null), TestZones.Copenhagen, raw)));

    [Fact]
    public void Update_rejects_a_local_time_in_the_dst_gap_and_resolves_an_overlap_to_the_earlier_instant()
    {
        var history = HistoryWith(null);

        Assert.Equal("cancelBy", Key(() => Resubmit(history, TestZones.Copenhagen, "2026-03-29T02:30")));
        Assert.Equal(Utc("2026-10-25T00:30:00Z"), Assert.IsType<SubscriptionCancelByChanged>(Assert.Single(Update(history, TestZones.Copenhagen, "2026-10-25T02:30"))).CancelBy);
    }

    [Fact]
    public void Update_that_changes_name_and_cancel_by_emits_both_events_once()
    {
        var history = HistoryWith(null);

        var events = Update(history, TestZones.Copenhagen, "2026-12-01T09:00", name: "Renamed");

        Assert.Equal(2, events.Count);
        Assert.Single(events.OfType<SubscriptionDetailsChanged>());
        Assert.Single(events.OfType<SubscriptionCancelByChanged>());
    }

    // ---- replay (D1) -------------------------------------------------------------------------------

    [Fact]
    public void Rehydrate_a_later_details_change_never_clears_the_cancel_by()
    {
        // D1 regression: DetailsChanged has no CancelBy member, so replaying it (old or new) cannot touch the deadline.
        var history = HistoryWith(Utc("2026-12-01T08:00:00Z"));
        history.Add(new SubscriptionDetailsChanged("Renamed", "Video", "notes", Now, null));

        var state = SubscriptionState.Rehydrate(history);

        Assert.Equal(Utc("2026-12-01T08:00:00Z"), state.CancelBy);
        Assert.Equal("Renamed", state.Name);
    }

    [Fact]
    public void Rehydrate_applies_set_change_and_clear_in_order()
    {
        var history = HistoryWith(Utc("2026-12-01T08:00:00Z"));
        Assert.Equal(Utc("2026-12-01T08:00:00Z"), SubscriptionState.Rehydrate(history).CancelBy);

        history.Add(new SubscriptionCancelByChanged(Utc("2026-12-02T08:00:00Z"), Now));
        Assert.Equal(Utc("2026-12-02T08:00:00Z"), SubscriptionState.Rehydrate(history).CancelBy);

        history.Add(new SubscriptionCancelByChanged(null, Now));
        Assert.Null(SubscriptionState.Rehydrate(history).CancelBy);
    }

    [Fact]
    public void Rehydrate_a_history_without_any_cancel_by_event_reads_null() => Assert.Null(SubscriptionState.Rehydrate(HistoryWith(null)).CancelBy);

    [Fact]
    public void The_event_is_registered_under_its_v1_alias() =>
        Assert.Equal(typeof(SubscriptionCancelByChanged), SubscriptionEventTypes.ByAlias["subscription.cancel_by_changed.v1"]);
}
