using Subscribed.Subscriptions.Domain;
using Xunit;

namespace Subscribed.UnitTests;

public sealed class SubscriptionPaymentOptionTests
{
    private const string OptionA = "0192f3c1a1b27c4e8d2e5f60718293b5";
    private const string OptionB = "0192f3c1a1b27c4e8d2e5f60718293c6";
    private static readonly DateTimeOffset Now = SubscriptionDecisionTests.Now;

    private static PriceEntryChange Entry(string from, string amount, string? option) =>
        SubscriptionDecisions.ParsePriceEntry(new PriceInput(from, amount, "month", 1, option));

    private static List<object> Added(string? catalogId = null, string? option = null) =>
        SubscriptionDecisionTests.Added(SubscriptionDecisionTests.Input() with { CatalogId = catalogId, PaymentOptionId = option });

    private static PriceEntryState Row(List<object> history, string entryId) =>
        SubscriptionState.Rehydrate(history).Entries.Single(e => e.EntryId == entryId);

    // ---- add -----------------------------------------------------------------------------------------

    [Fact]
    public void Add_with_an_initial_option_appends_added_catalog_linked_payment_changed_in_that_order()
    {
        var events = Added("streamco", OptionA);
        Assert.Collection(
            events,
            e => Assert.IsType<SubscriptionAdded>(e),
            e => Assert.IsType<SubscriptionCatalogLinked>(e),
            e =>
            {
                var changed = Assert.IsType<SubscriptionPriceEntryPaymentChanged>(e);
                Assert.Equal("e1", changed.EntryId);
                Assert.Equal(OptionA, changed.PaymentOptionId);
            });
        var state = SubscriptionState.Rehydrate(events);
        Assert.Equal(3, state.Version);
        Assert.Equal(OptionA, state.Entries.Single().PaymentOptionId);
    }

    [Fact]
    public void Add_with_an_option_but_no_catalog_link_appends_added_then_payment_changed()
    {
        Assert.Collection(Added(null, OptionA), e => Assert.IsType<SubscriptionAdded>(e), e => Assert.IsType<SubscriptionPriceEntryPaymentChanged>(e));
    }

    [Fact]
    public void Add_without_an_option_emits_no_payment_event_and_the_entry_has_none()
    {
        var events = Added();
        Assert.Single(events);
        Assert.Null(SubscriptionState.Rehydrate(events).Entries.Single().PaymentOptionId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_option_id_becomes_null(string blank)
    {
        Assert.Null(SubscriptionDecisions.ParseAdd(SubscriptionDecisionTests.Input() with { PaymentOptionId = blank }).PaymentOptionId);
        Assert.Null(Entry("2025-01-01", "1.00", blank).PaymentOptionId);
    }

    [Fact]
    public void Option_id_is_trimmed()
    {
        Assert.Equal(OptionA, Entry("2025-01-01", "1.00", "  " + OptionA + " ").PaymentOptionId);
    }

    [Theory]
    [InlineData("nope")]
    [InlineData("0192F3C1A1B27C4E8D2E5F60718293B5")]
    [InlineData("0192f3c1a1b27c4e8d2e5f60718293b")]
    [InlineData("paymentoption-0192f3c1a1b27c4e8d2e5f60718293b5")]
    public void Malformed_option_id_is_a_field_error(string id)
    {
        var ex = Assert.Throws<SubscriptionValidationException>(() => Entry("2025-01-01", "1.00", id));
        Assert.Equal("paymentOptionId", Assert.Single(ex.Errors).Key);
        Assert.Throws<SubscriptionValidationException>(() => SubscriptionDecisions.ParseAdd(SubscriptionDecisionTests.Input() with { PaymentOptionId = id }));
    }

    // ---- add and correct price entries -------------------------------------------------------------

    [Fact]
    public void Adding_a_price_entry_with_an_option_emits_added_then_payment_changed()
    {
        var history = Added(null, OptionA);
        var events = SubscriptionDecisions.AddPriceEntry(history, Entry("2025-01-01", "149.00", OptionB), "e2", Now);
        Assert.Collection(events, e => Assert.IsType<SubscriptionPriceEntryAdded>(e), e => Assert.Equal(OptionB, Assert.IsType<SubscriptionPriceEntryPaymentChanged>(e).PaymentOptionId));
        history.AddRange(events);
        Assert.Equal(OptionA, Row(history, "e1").PaymentOptionId);
        Assert.Equal(OptionB, Row(history, "e2").PaymentOptionId);
    }

    [Fact]
    public void Adding_a_price_entry_without_an_option_emits_only_the_added_event()
    {
        var history = Added(null, OptionA);
        Assert.IsType<SubscriptionPriceEntryAdded>(Assert.Single(SubscriptionDecisions.AddPriceEntry(history, Entry("2025-01-01", "149.00", null), "e2", Now)));
    }

    [Fact]
    public void Correct_with_only_the_option_changed_emits_one_payment_event()
    {
        var history = Added(null, OptionA);
        var events = SubscriptionDecisions.CorrectPriceEntry(history, "e1", Entry("2024-03-01", "129.00", OptionB), Now);
        Assert.Equal(OptionB, Assert.IsType<SubscriptionPriceEntryPaymentChanged>(Assert.Single(events)).PaymentOptionId);
        history.AddRange(events);
        Assert.Equal(OptionB, Row(history, "e1").PaymentOptionId);
    }

    [Fact]
    public void Correct_with_both_price_and_option_changed_emits_two_events_and_raises_the_version_by_two()
    {
        var history = Added(null, OptionA);
        var before = SubscriptionState.Rehydrate(history).Version;
        var events = SubscriptionDecisions.CorrectPriceEntry(history, "e1", Entry("2024-03-01", "139.00", OptionB), Now);
        Assert.Collection(events, e => Assert.IsType<SubscriptionPriceEntryCorrected>(e), e => Assert.IsType<SubscriptionPriceEntryPaymentChanged>(e));
        history.AddRange(events);
        Assert.Equal(before + 2, SubscriptionState.Rehydrate(history).Version);
    }

    [Fact]
    public void Correct_with_only_the_price_changed_and_the_option_kept_emits_no_payment_event()
    {
        var history = Added(null, OptionA);
        var events = SubscriptionDecisions.CorrectPriceEntry(history, "e1", Entry("2024-03-01", "139.00", OptionA), Now);
        Assert.IsType<SubscriptionPriceEntryCorrected>(Assert.Single(events));
    }

    [Fact]
    public void Correct_with_no_change_emits_no_events()
    {
        var history = Added(null, OptionA);
        Assert.Empty(SubscriptionDecisions.CorrectPriceEntry(history, "e1", Entry("2024-03-01", "129.00", OptionA), Now));
        var none = Added();
        Assert.Empty(SubscriptionDecisions.CorrectPriceEntry(none, "e1", Entry("2024-03-01", "129.00", null), Now));
    }

    [Fact]
    public void Clearing_an_option_emits_one_event_and_replays_to_null()
    {
        var history = Added(null, OptionA);
        var events = SubscriptionDecisions.CorrectPriceEntry(history, "e1", Entry("2024-03-01", "129.00", null), Now);
        var cleared = Assert.IsType<SubscriptionPriceEntryPaymentChanged>(Assert.Single(events));
        Assert.Null(cleared.PaymentOptionId);
        history.AddRange(events);
        Assert.Null(Row(history, "e1").PaymentOptionId);
    }

    [Fact]
    public void Assignments_over_time_are_kept_per_entry()
    {
        var history = Added(null, OptionA);
        history.AddRange(SubscriptionDecisions.AddPriceEntry(history, Entry("2025-01-01", "149.00", OptionB), "e2", Now));
        var state = SubscriptionState.Rehydrate(history);
        Assert.Equal([OptionA, OptionB], state.Entries.Select(e => e.PaymentOptionId));
        Assert.Equal([OptionA, OptionB], SubscriptionCalculator.History(state, SubscriptionDecisionTests.Today).Select(r => r.Entry.PaymentOptionId));
    }

    // ---- details, replay compatibility --------------------------------------------------------------

    [Fact]
    public void The_details_update_neither_sets_nor_clears_the_option()
    {
        var history = Added(null, OptionA);
        var current = SubscriptionState.Rehydrate(history);
        var update = new DetailsUpdate("Renamed", current.Category, current.Notes, current.Currency, current.StartDate, current.EndDate, current.CatalogId);
        var events = SubscriptionDecisions.UpdateDetails(history, update, Now);
        Assert.IsType<SubscriptionDetailsChanged>(Assert.Single(events));
        history.AddRange(events);
        Assert.Equal(OptionA, Row(history, "e1").PaymentOptionId);
    }

    [Fact]
    public void A_history_without_payment_events_rehydrates_exactly_as_before()
    {
        var state = SubscriptionState.Rehydrate(SubscriptionDecisionTests.FilmPlus());
        Assert.Equal(3, state.Version);
        Assert.All(state.Entries, e => Assert.Null(e.PaymentOptionId));
    }

    [Fact]
    public void A_payment_event_for_a_removed_entry_does_not_throw_and_the_entry_does_not_reappear()
    {
        var history = Added();
        history.AddRange(SubscriptionDecisions.AddPriceEntry(history, Entry("2025-01-01", "149.00", null), "e2", Now));
        history.AddRange(SubscriptionDecisions.RemovePriceEntry(history, "e2", Now));
        history.Add(new SubscriptionPriceEntryPaymentChanged("e2", OptionA, Now));
        var state = SubscriptionState.Rehydrate(history);
        Assert.Equal(["e1"], state.Entries.Select(e => e.EntryId));
        Assert.Equal(4, state.Version);
    }

    [Fact]
    public void A_payment_event_for_an_unknown_entry_does_not_throw()
    {
        var history = Added();
        history.Add(new SubscriptionPriceEntryPaymentChanged("nope", OptionA, Now));
        var state = SubscriptionState.Rehydrate(history);
        Assert.Equal(2, state.Version);
        Assert.Null(state.Entries.Single().PaymentOptionId);
    }

    [Fact]
    public void The_new_event_is_registered_under_its_own_alias()
    {
        Assert.Equal("subscription.price_entry_payment_changed.v1", SubscriptionEventTypes.PriceEntryPaymentChanged);
        Assert.Equal(typeof(SubscriptionPriceEntryPaymentChanged), SubscriptionEventTypes.Resolve(SubscriptionEventTypes.PriceEntryPaymentChanged));
    }

    // ---- Edit form: option change in the details update (#39) ----------------------------------------

    private static readonly DateTimeOffset EndOfDay = new(2026, 11, 13, 23, 59, 59, TimeSpan.Zero);
    private static readonly DateTimeOffset StartOfNextDay = new(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The details exactly as stored, plus an optional option change and optional period overrides.</summary>
    private static DetailsUpdate Edit(List<object> history, PaymentOptionChange? change, string? start = null, string? end = null, bool clearEnd = false, string? currency = null)
    {
        var s = SubscriptionState.Rehydrate(history);
        return new DetailsUpdate(
            s.Name,
            s.Category,
            s.Notes,
            currency ?? s.Currency,
            start is null ? s.StartDate : DateOnly.Parse(start, System.Globalization.CultureInfo.InvariantCulture),
            clearEnd ? null : end is null ? s.EndDate : DateOnly.Parse(end, System.Globalization.CultureInfo.InvariantCulture),
            s.CatalogId,
            s.UnsubscribeUrl,
            change);
    }

    private static IReadOnlyList<object> Decide(List<object> history, DetailsUpdate update, DateTimeOffset? now = null, string newId = "n1") =>
        SubscriptionDecisions.UpdateDetails(history, update, newId, now ?? Now);

    private static string Field(Action act) => Assert.Single(Assert.Throws<SubscriptionValidationException>(act).Errors).Key;

    private static List<object> WithEntry(List<object> history, string from, string entryId, string? option = null)
    {
        history.AddRange(SubscriptionDecisions.AddPriceEntry(history, Entry(from, "149.00", option), entryId, Now));
        return history;
    }

    [Fact]
    public void Edit_different_option_on_an_entry_that_started_before_today_adds_an_entry_for_today_with_the_same_price()
    {
        var history = Added(null, OptionA);
        var events = Decide(history, Edit(history, new PaymentOptionChange(OptionB)));
        Assert.Collection(
            events,
            e =>
            {
                var added = Assert.IsType<SubscriptionPriceEntryAdded>(e);
                Assert.Equal("n1", added.EntryId);
                Assert.Equal(SubscriptionDecisionTests.Today, added.EffectiveFrom);
                Assert.Equal(129.00m, added.Amount);
                Assert.Equal("month", added.CadenceUnit);
                Assert.Equal(1, added.CadenceCount);
            },
            e =>
            {
                var changed = Assert.IsType<SubscriptionPriceEntryPaymentChanged>(e);
                Assert.Equal("n1", changed.EntryId);
                Assert.Equal(OptionB, changed.PaymentOptionId);
            });
        history.AddRange(events);
        Assert.Equal(OptionA, Row(history, "e1").PaymentOptionId);
        Assert.Equal(OptionB, Row(history, "n1").PaymentOptionId);
    }

    [Fact]
    public void Edit_when_the_current_entry_starts_today_updates_it_in_place()
    {
        var history = Added();
        WithEntry(history, "2026-11-13", "e2", OptionA);
        var changed = Assert.IsType<SubscriptionPriceEntryPaymentChanged>(Assert.Single(Decide(history, Edit(history, new PaymentOptionChange(OptionB)))));
        Assert.Equal("e2", changed.EntryId);
        Assert.Equal(OptionB, changed.PaymentOptionId);
    }

    [Fact]
    public void Edit_when_the_subscription_has_not_started_updates_the_initial_entry_in_place()
    {
        var history = SubscriptionDecisionTests.Added(SubscriptionDecisionTests.Input(start: "2027-01-01"));
        var changed = Assert.IsType<SubscriptionPriceEntryPaymentChanged>(Assert.Single(Decide(history, Edit(history, new PaymentOptionChange(OptionA)))));
        Assert.Equal("e1", changed.EntryId);
    }

    [Fact]
    public void Edit_not_started_with_a_later_entry_emits_exactly_one_event_per_entry()
    {
        var history = SubscriptionDecisionTests.Added(SubscriptionDecisionTests.Input(start: "2027-01-01"));
        WithEntry(history, "2027-06-01", "e2");
        var events = Decide(history, Edit(history, new PaymentOptionChange(OptionA)));
        Assert.Equal(["e1", "e2"], events.Select(e => Assert.IsType<SubscriptionPriceEntryPaymentChanged>(e).EntryId));
    }

    [Fact]
    public void Edit_when_the_current_entry_starts_today_and_an_upcoming_entry_exists_changes_both_without_adding()
    {
        var history = Added();
        WithEntry(history, "2026-11-13", "e2");
        WithEntry(history, "2027-03-01", "e3");
        var events = Decide(history, Edit(history, new PaymentOptionChange(OptionA)));
        Assert.Equal(["e2", "e3"], events.Select(e => Assert.IsType<SubscriptionPriceEntryPaymentChanged>(e).EntryId));
    }

    [Fact]
    public void Edit_adds_an_entry_and_also_sets_the_option_on_every_upcoming_entry()
    {
        var history = Added();
        WithEntry(history, "2027-03-01", "e2");
        WithEntry(history, "2027-09-01", "e3");
        var events = Decide(history, Edit(history, new PaymentOptionChange(OptionA)));
        Assert.IsType<SubscriptionPriceEntryAdded>(events[0]);
        Assert.Equal(["n1", "e2", "e3"], events.OfType<SubscriptionPriceEntryPaymentChanged>().Select(e => e.EntryId));
        history.AddRange(events);
        Assert.All(SubscriptionState.Rehydrate(history).Entries.Where(e => e.EntryId != "e1"), e => Assert.Equal(OptionA, e.PaymentOptionId));
    }

    [Fact]
    public void Edit_skips_an_upcoming_entry_that_already_has_the_option()
    {
        var history = Added();
        WithEntry(history, "2027-03-01", "e2", OptionA);
        WithEntry(history, "2027-09-01", "e3");
        var events = Decide(history, Edit(history, new PaymentOptionChange(OptionA)));
        Assert.Equal(["n1", "e3"], events.OfType<SubscriptionPriceEntryPaymentChanged>().Select(e => e.EntryId));
    }

    [Fact]
    public void Edit_with_the_unchanged_option_or_no_change_emits_nothing()
    {
        var history = Added(null, OptionA);
        WithEntry(history, "2027-03-01", "e2");
        Assert.Empty(Decide(history, Edit(history, new PaymentOptionChange(OptionA))));
        Assert.Empty(Decide(history, Edit(history, null)));
    }

    [Fact]
    public void Edit_with_an_unchanged_removed_option_emits_nothing()
    {
        // The state has no notion of removal: an entry that still points at a removed option is simply "the same option".
        var history = Added(null, OptionA);
        Assert.Empty(Decide(history, Edit(history, new PaymentOptionChange(OptionA))));
    }

    [Fact]
    public void Edit_none_on_an_entry_with_an_option_adds_an_entry_without_a_payment_event()
    {
        var history = Added(null, OptionA);
        var added = Assert.IsType<SubscriptionPriceEntryAdded>(Assert.Single(Decide(history, Edit(history, new PaymentOptionChange(null)))));
        Assert.Equal(SubscriptionDecisionTests.Today, added.EffectiveFrom);
    }

    [Fact]
    public void Edit_none_on_a_same_day_entry_emits_payment_changed_null()
    {
        var history = Added();
        WithEntry(history, "2026-11-13", "e2", OptionA);
        var changed = Assert.IsType<SubscriptionPriceEntryPaymentChanged>(Assert.Single(Decide(history, Edit(history, new PaymentOptionChange(null)))));
        Assert.Equal("e2", changed.EntryId);
        Assert.Null(changed.PaymentOptionId);
    }

    [Fact]
    public void Edit_none_clears_an_upcoming_entry_that_has_an_option_and_leaves_one_without()
    {
        var history = Added(null, OptionA);
        WithEntry(history, "2027-03-01", "e2", OptionA);
        WithEntry(history, "2027-09-01", "e3");
        var events = Decide(history, Edit(history, new PaymentOptionChange(null)));
        Assert.IsType<SubscriptionPriceEntryAdded>(events[0]);
        var changed = Assert.IsType<SubscriptionPriceEntryPaymentChanged>(Assert.Single(events.Skip(1)));
        Assert.Equal("e2", changed.EntryId);
        Assert.Null(changed.PaymentOptionId);
    }

    [Fact]
    public void Edit_on_an_ended_subscription_with_a_different_option_is_a_payment_option_error()
    {
        var history = SubscriptionDecisionTests.Added(SubscriptionDecisionTests.Input(end: "2026-11-12"));
        Assert.Equal("paymentOption", Field(() => Decide(history, Edit(history, new PaymentOptionChange(OptionA)))));
    }

    [Fact]
    public void Edit_on_an_ended_subscription_with_an_unchanged_option_still_saves_the_details()
    {
        var history = SubscriptionDecisionTests.Added(SubscriptionDecisionTests.Input(end: "2026-11-12"));
        var update = Edit(history, new PaymentOptionChange(null)) with { Name = "Renamed" };
        Assert.IsType<SubscriptionDetailsChanged>(Assert.Single(Decide(history, update)));
    }

    [Fact]
    public void Edit_moving_the_end_date_to_the_past_together_with_an_option_change_is_rejected_whole()
    {
        var history = Added();
        Assert.Equal("paymentOption", Field(() => Decide(history, Edit(history, new PaymentOptionChange(OptionA), end: "2026-11-12"))));
    }

    [Fact]
    public void Edit_with_a_start_date_in_the_future_clamps_the_current_entry_and_updates_it_in_place()
    {
        var history = Added();
        var events = Decide(history, Edit(history, new PaymentOptionChange(OptionA), start: "2027-01-01"));
        Assert.IsType<SubscriptionPeriodChanged>(events[0]);
        var changed = Assert.IsType<SubscriptionPriceEntryPaymentChanged>(events[1]);
        Assert.Equal("e1", changed.EntryId);
        Assert.Equal(2, events.Count);
    }

    [Theory]
    [InlineData("2026-11-13")]
    [InlineData("2026-11-14")]
    public void Edit_with_an_end_date_today_or_tomorrow_is_not_ended_and_adds_an_entry(string end)
    {
        var history = Added();
        var events = Decide(history, Edit(history, new PaymentOptionChange(OptionA), end: end));
        Assert.Contains(events, e => e is SubscriptionPriceEntryAdded);
        Assert.Contains(events, e => e is SubscriptionPeriodChanged);
    }

    [Fact]
    public void Edit_today_follows_the_utc_date_and_flips_the_branch_across_midnight()
    {
        var history = Added();
        WithEntry(history, "2026-11-13", "e2", OptionA);
        var before = Decide(history, Edit(history, new PaymentOptionChange(OptionB)), EndOfDay);
        var changed = Assert.IsType<SubscriptionPriceEntryPaymentChanged>(Assert.Single(before));
        Assert.Equal("e2", changed.EntryId);

        var after = Decide(history, Edit(history, new PaymentOptionChange(OptionB)), StartOfNextDay);
        var added = Assert.IsType<SubscriptionPriceEntryAdded>(after[0]);
        Assert.Equal(new DateOnly(2026, 11, 14), added.EffectiveFrom);
        Assert.IsType<SubscriptionPriceEntryPaymentChanged>(after[1]);
    }

    [Fact]
    public void Edit_on_a_paused_subscription_applies_the_change()
    {
        var history = Added();
        history.AddRange(SubscriptionDecisions.Pause(history, Now));
        var events = Decide(history, Edit(history, new PaymentOptionChange(OptionA)));
        Assert.IsType<SubscriptionPriceEntryAdded>(events[0]);
    }

    [Fact]
    public void Edit_details_and_option_change_come_out_as_one_combined_list()
    {
        var history = Added();
        var update = Edit(history, new PaymentOptionChange(OptionA)) with { Name = "Renamed" };
        var events = Decide(history, update);
        Assert.Collection(
            events,
            e => Assert.IsType<SubscriptionDetailsChanged>(e),
            e => Assert.IsType<SubscriptionPriceEntryAdded>(e),
            e => Assert.IsType<SubscriptionPriceEntryPaymentChanged>(e));
    }

    [Fact]
    public void Edit_with_a_failing_detail_rejects_the_option_change_too()
    {
        var history = Added();
        var update = Edit(history, new PaymentOptionChange(OptionA), start: "2027-01-01", end: "2026-12-01");
        Assert.Equal("endDate", Field(() => Decide(history, update)));
    }

    [Fact]
    public void Edit_with_an_option_change_and_a_currency_change_still_checks_the_copied_price()
    {
        var history = SubscriptionDecisionTests.Added(SubscriptionDecisionTests.Input(amount: "129.50"));
        var update = Edit(history, new PaymentOptionChange(OptionA), currency: "JPY");
        Assert.Equal("currency", Field(() => Decide(history, update)));
    }

    [Fact]
    public void Edit_none_on_a_current_entry_whose_option_was_removed_adds_an_entry_without_a_payment_event()
    {
        var history = Added(null, OptionA);
        Assert.IsType<SubscriptionPriceEntryAdded>(Assert.Single(Decide(history, Edit(history, new PaymentOptionChange(null)))));
    }

    [Fact]
    public void The_old_update_details_signature_still_works_without_an_option_change()
    {
        var history = Added();
        var update = Edit(history, null) with { Name = "Renamed" };
        Assert.IsType<SubscriptionDetailsChanged>(Assert.Single(SubscriptionDecisions.UpdateDetails(history, update, Now)));
    }

    // ---- Edit form: shape of the request -------------------------------------------------------------

    private static UpdateInput UpdateInputWith(bool set, bool idSpecified, string? id) =>
        new("StreamCo", null, null, "DKK", "2024-03-01", null, null, null, set, idSpecified, id);

    [Fact]
    public void Parse_update_without_a_payment_option_object_carries_no_change()
    {
        Assert.Null(SubscriptionDecisions.ParseUpdate(UpdateInputWith(false, false, null)).PaymentOption);
    }

    [Fact]
    public void Parse_update_with_an_id_or_an_explicit_null_id_carries_the_change()
    {
        Assert.Equal(OptionA, SubscriptionDecisions.ParseUpdate(UpdateInputWith(true, true, "  " + OptionA + " ")).PaymentOption!.PaymentOptionId);
        Assert.Null(SubscriptionDecisions.ParseUpdate(UpdateInputWith(true, true, null)).PaymentOption!.PaymentOptionId);
        Assert.Null(SubscriptionDecisions.ParseUpdate(UpdateInputWith(true, true, "  ")).PaymentOption!.PaymentOptionId);
    }

    [Fact]
    public void Parse_update_with_an_object_but_no_id_is_a_payment_option_error()
    {
        Assert.Equal("paymentOption", Field(() => SubscriptionDecisions.ParseUpdate(UpdateInputWith(true, false, null))));
    }

    [Fact]
    public void Parse_update_with_a_malformed_id_is_a_payment_option_id_error()
    {
        Assert.Equal("paymentOptionId", Field(() => SubscriptionDecisions.ParseUpdate(UpdateInputWith(true, true, "nope"))));
    }
}
