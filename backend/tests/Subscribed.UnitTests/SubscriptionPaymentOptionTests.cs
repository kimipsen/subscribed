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
}
