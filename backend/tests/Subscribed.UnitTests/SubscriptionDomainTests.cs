using System.Globalization;
using System.Text.Json;
using Subscribed.Subscriptions.Domain;
using Xunit;

namespace Subscribed.UnitTests;

public sealed class SubscriptionEventTests
{
    [Theory]
    [InlineData(SubscriptionEventTypes.Added, typeof(SubscriptionAdded))]
    [InlineData(SubscriptionEventTypes.DetailsChanged, typeof(SubscriptionDetailsChanged))]
    [InlineData(SubscriptionEventTypes.CurrencyCorrected, typeof(SubscriptionCurrencyCorrected))]
    [InlineData(SubscriptionEventTypes.PeriodChanged, typeof(SubscriptionPeriodChanged))]
    [InlineData(SubscriptionEventTypes.PriceEntryAdded, typeof(SubscriptionPriceEntryAdded))]
    [InlineData(SubscriptionEventTypes.PriceEntryCorrected, typeof(SubscriptionPriceEntryCorrected))]
    [InlineData(SubscriptionEventTypes.PriceEntryRemoved, typeof(SubscriptionPriceEntryRemoved))]
    [InlineData(SubscriptionEventTypes.Removed, typeof(SubscriptionRemoved))]
    public void Golden_fixtures_deserialize_and_round_trip(string alias, Type type)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "events", alias + ".json")).Trim();
        var e = SubscriptionEventTypes.Deserialize(alias, json);
        Assert.IsType(type, e);
        Assert.Equal(alias, SubscriptionEventTypes.AliasOf(type));

        // Every fixture property maps to a non-null CLR property, and every CLR property is in the fixture.
        var fixtureNames = JsonElement.Parse(json).EnumerateObject().Select(p => p.Name).Order(StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(type.GetProperties().Select(p => p.Name).Order(StringComparer.OrdinalIgnoreCase).ToList(), fixtureNames, StringComparer.OrdinalIgnoreCase);
        foreach (var name in fixtureNames)
        {
            Assert.NotNull(type.GetProperties().Single(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).GetValue(e));
        }

        Assert.Equal(e, SubscriptionEventTypes.Deserialize(alias, SubscriptionEventTypes.Serialize(e)));
    }

    [Fact]
    public void Amounts_serialize_as_exact_json_numbers_and_dates_as_iso_strings()
    {
        var json = SubscriptionEventTypes.Serialize(new SubscriptionPriceEntryAdded("e", new DateOnly(2024, 3, 1), 11.99m, "month", 1, DateTimeOffset.UnixEpoch));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Number, doc.RootElement.GetProperty("amount").ValueKind);
        Assert.Equal("11.99", doc.RootElement.GetProperty("amount").GetRawText());
        Assert.Equal("2024-03-01", doc.RootElement.GetProperty("effectiveFrom").GetString());
    }

    [Fact]
    public void Unknown_alias_fails_loudly()
    {
        var ex = Assert.Throws<UnknownSubscriptionEventAliasException>(() => SubscriptionEventTypes.Deserialize("subscription.bogus.v1", "{}"));
        Assert.Equal("subscription.bogus.v1", ex.Alias);
        Assert.Throws<UnknownSubscriptionEventAliasException>(() => SubscriptionEventTypes.Resolve("subscription.added.v2"));
    }

    [Fact]
    public void Events_carry_no_email_token_or_floating_point_fields()
    {
        foreach (var type in SubscriptionEventTypes.ByAlias.Values)
        {
            Assert.DoesNotContain(type.GetProperties(), p => p.Name.Contains("Email", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Token", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(type.GetProperties(), p => p.PropertyType == typeof(double) || p.PropertyType == typeof(float));
            Assert.Contains(type.GetProperties(), p => p.Name == "RecordedAt");
        }

        Assert.Equal(8, SubscriptionEventTypes.ByAlias.Count);
        Assert.All(SubscriptionEventTypes.ByAlias.Keys, a => Assert.Matches(@"^subscription\.[a-z_]+\.v1$", a));
    }

    [Fact]
    public void State_has_no_floating_point_members()
    {
        foreach (var type in new[] { typeof(SubscriptionState), typeof(PriceEntryState) })
        {
            Assert.DoesNotContain(type.GetProperties(), p => p.PropertyType == typeof(double) || p.PropertyType == typeof(float));
        }
    }
}

public sealed class SubscriptionDecisionTests
{
    internal static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-13T12:00:00Z", CultureInfo.InvariantCulture);
    internal static readonly DateOnly Today = new(2026, 11, 13);

    internal static AddInput Input(string name = "StreamCo", string currency = "DKK", string amount = "129.00", string unit = "month", int count = 1, string start = "2024-03-01", string? end = null) =>
        new(name, null, null, currency, start, end, amount, unit, count);

    internal static List<object> Added(AddInput input, string entryId = "e1")
    {
        var events = SubscriptionDecisions.Add(SubscriptionDecisions.ParseAdd(input), "household-default", entryId, Now);
        return [.. events];
    }

    internal static PriceEntryChange Entry(string from, string amount, string unit = "month", int count = 1) =>
        SubscriptionDecisions.ParsePriceEntry(new PriceInput(from, amount, unit, count));

    internal static List<object> Apply(List<object> history, IReadOnlyList<object> events)
    {
        history.AddRange(events);
        return history;
    }

    /// <summary>EX-007: 59.00 DKK monthly from 2021-01-01, 69.00 from 2022-06-01 and 79.00 from 2023-11-01.</summary>
    internal static List<object> FilmPlus()
    {
        var history = Added(Input("FilmPlus", amount: "59.00", start: "2021-01-01"));
        Apply(history, SubscriptionDecisions.AddPriceEntry(history, Entry("2022-06-01", "69.00"), "e2", Now));
        Apply(history, SubscriptionDecisions.AddPriceEntry(history, Entry("2023-11-01", "79.00"), "e3", Now));
        return history;
    }

    private static string Key(Action action) => Assert.Single(Assert.Throws<SubscriptionValidationException>(action).Errors).Key;

    private static SubscriptionValidationException Rejected(Action action) => Assert.Throws<SubscriptionValidationException>(action);

    // ---- shape -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("", "name")]
    [InlineData("   ", "name")]
    public void EX_013_empty_name(string name, string key) => Assert.Equal(key, Key(() => SubscriptionDecisions.ParseAdd(Input(name))));

    [Fact]
    public void EX_013_name_of_81_characters() => Assert.Equal("name", Key(() => SubscriptionDecisions.ParseAdd(Input(new string('a', 81)))));

    [Fact]
    public void EX_013_unknown_currency() => Assert.Equal("currency", Key(() => SubscriptionDecisions.ParseAdd(Input(currency: "XYZ"))));

    [Theory]
    [InlineData("-5.00")]
    [InlineData("1000000000")]
    [InlineData("129,00")]
    [InlineData("+129")]
    [InlineData("1e3")]
    [InlineData("abc")]
    [InlineData("١٢٣")]
    public void Invalid_amount_shapes(string amount) => Assert.Equal("amount", Key(() => SubscriptionDecisions.ParseAdd(Input(amount: amount))));

    [Fact]
    public void EX_013_minor_unit_precision_on_add()
    {
        Assert.Equal("amount", Key(() => SubscriptionDecisions.ParseAdd(Input(amount: "99.955"))));
        Assert.Equal("amount", Key(() => SubscriptionDecisions.ParseAdd(Input(currency: "JPY", amount: "1200.5"))));
        Assert.Equal("amount", Key(() => SubscriptionDecisions.ParseAdd(Input(currency: "JPY", amount: "1200.0"))));
        Assert.Equal("amount", Key(() => SubscriptionDecisions.ParseAdd(Input(currency: "KWD", amount: "1.2345"))));
        Assert.Equal(1.234m, SubscriptionDecisions.ParseAdd(Input(currency: "KWD", amount: "1.234")).Amount);
    }

    [Theory]
    [InlineData("week", 0)]
    [InlineData("week", 100)]
    [InlineData("Month", 1)]
    [InlineData("day", 1)]
    public void Invalid_cadence(string unit, int count)
    {
        var errors = Rejected(() => SubscriptionDecisions.ParseAdd(Input(unit: unit, count: count))).Errors;
        Assert.Contains(errors.Keys, k => k is "cadenceUnit" or "cadenceCount");
    }

    [Fact]
    public void Missing_required_fields_are_each_reported_and_collected_together()
    {
        var errors = Rejected(() => SubscriptionDecisions.ParseAdd(new AddInput(null, null, null, null, null, null, null, null, null))).Errors;
        Assert.Equal(["amount", "cadenceCount", "cadenceUnit", "currency", "name", "startDate"], errors.Keys.Order(StringComparer.Ordinal));
        var several = Rejected(() => SubscriptionDecisions.ParseAdd(Input("", "XYZ", "-1", "day", 0, "2024-13-01"))).Errors;
        Assert.Equal(["amount", "cadenceCount", "cadenceUnit", "currency", "name", "startDate"], several.Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("2024-3-01")]
    [InlineData("2024-03-1")]
    [InlineData("01/03/2024")]
    [InlineData("2024-02-30")]
    [InlineData("2024-03-01T00:00:00")]
    [InlineData(" 2024-03-01")]
    [InlineData("")]
    public void Dates_must_be_yyyy_MM_dd(string date) => Assert.Equal("startDate", Key(() => SubscriptionDecisions.ParseAdd(Input(start: date))));

    [Fact]
    public void Text_fields_are_normalized_and_checked()
    {
        var parsed = SubscriptionDecisions.ParseAdd(Input() with { Name = "  Stream  ", Category = "  ", Notes = "a\r\nb" });
        Assert.Equal("Stream", parsed.Name);
        Assert.Null(parsed.Category);
        Assert.Equal("a\nb", parsed.Notes);
        Assert.Equal("category", Key(() => SubscriptionDecisions.ParseAdd(Input() with { Category = new string('c', 41) })));
        Assert.Equal("notes", Key(() => SubscriptionDecisions.ParseAdd(Input() with { Notes = "a\tb" })));
    }

    // ---- add and invariants ------------------------------------------------------------------------

    [Fact]
    public void Add_emits_one_event_in_utc_with_the_initial_entry()
    {
        var offset = new DateTimeOffset(2026, 11, 13, 14, 0, 0, TimeSpan.FromHours(2));
        var events = SubscriptionDecisions.Add(SubscriptionDecisions.ParseAdd(Input()), "household-default", "e1", offset);
        var added = Assert.IsType<SubscriptionAdded>(Assert.Single(events));
        Assert.Equal(TimeSpan.Zero, added.RecordedAt.Offset);
        Assert.Equal(offset.UtcDateTime, added.RecordedAt.UtcDateTime);
        var state = SubscriptionState.Rehydrate(events);
        Assert.Equal(1, state.Version);
        Assert.Equal(new DateOnly(2024, 3, 1), Assert.Single(state.Entries).EffectiveFrom);
    }

    [Fact]
    public void EX_013_end_before_start_is_rejected_on_add() =>
        Assert.Equal("endDate", Key(() => SubscriptionDecisions.Add(SubscriptionDecisions.ParseAdd(Input(start: "2024-03-01", end: "2024-01-01")), "h", "e1", Now)));

    [Fact]
    public void EX_014_moving_the_start_date_earlier_moves_the_initial_entry()
    {
        var history = Added(Input());
        Apply(history, SubscriptionDecisions.AddPriceEntry(history, Entry("2025-01-01", "149.00"), "e2", Now));
        var events = SubscriptionDecisions.UpdateDetails(history, new DetailsUpdate("StreamCo", null, null, "DKK", new DateOnly(2023, 6, 1), null), Now);
        var period = Assert.IsType<SubscriptionPeriodChanged>(Assert.Single(events));
        Assert.Equal(new DateOnly(2023, 6, 1), period.StartDate);
        var state = SubscriptionState.Rehydrate(history.Concat(events));
        Assert.Equal(new DateOnly(2023, 6, 1), state.Entries.Single(e => e.EntryId == "e1").EffectiveFrom);
    }

    [Fact]
    public void EX_014_moving_the_start_date_past_a_later_entry_is_rejected()
    {
        var history = Added(Input());
        Apply(history, SubscriptionDecisions.AddPriceEntry(history, Entry("2025-01-01", "149.00"), "e2", Now));
        Assert.Equal("startDate", Key(() => SubscriptionDecisions.UpdateDetails(history, new DetailsUpdate("StreamCo", null, null, "DKK", new DateOnly(2025, 2, 1), null), Now)));
        Assert.Equal("startDate", Key(() => SubscriptionDecisions.UpdateDetails(history, new DetailsUpdate("StreamCo", null, null, "DKK", new DateOnly(2025, 1, 1), null), Now)));
    }

    [Fact]
    public void Ending_before_an_upcoming_price_is_rejected_and_ending_after_it_is_fine()
    {
        var history = Added(Input());
        Apply(history, SubscriptionDecisions.AddPriceEntry(history, Entry("2025-01-01", "149.00"), "e2", Now));
        Assert.Equal("endDate", Key(() => SubscriptionDecisions.UpdateDetails(history, new DetailsUpdate("StreamCo", null, null, "DKK", new DateOnly(2024, 3, 1), new DateOnly(2024, 12, 31)), Now)));
        Assert.Equal("endDate", Key(() => SubscriptionDecisions.UpdateDetails(history, new DetailsUpdate("StreamCo", null, null, "DKK", new DateOnly(2024, 3, 1), new DateOnly(2024, 1, 1)), Now)));
        Assert.IsType<SubscriptionPeriodChanged>(Assert.Single(SubscriptionDecisions.UpdateDetails(history, new DetailsUpdate("StreamCo", null, null, "DKK", new DateOnly(2024, 3, 1), new DateOnly(2025, 1, 1)), Now)));
    }

    [Fact]
    public void Ending_and_reinstating_set_and_clear_the_end_date()
    {
        var history = Added(Input());
        var ended = SubscriptionDecisions.UpdateDetails(history, new DetailsUpdate("StreamCo", null, null, "DKK", new DateOnly(2024, 3, 1), new DateOnly(2026, 11, 13)), Now);
        Apply(history, ended);
        Assert.Equal(new DateOnly(2026, 11, 13), SubscriptionState.Rehydrate(history).EndDate);
        Apply(history, SubscriptionDecisions.UpdateDetails(history, new DetailsUpdate("StreamCo", null, null, "DKK", new DateOnly(2024, 3, 1), null), Now));
        Assert.Null(SubscriptionState.Rehydrate(history).EndDate);
    }

    [Fact]
    public void An_unchanged_update_produces_no_events()
    {
        var history = Added(Input());
        Assert.Empty(SubscriptionDecisions.UpdateDetails(history, new DetailsUpdate("StreamCo", null, null, "DKK", new DateOnly(2024, 3, 1), null), Now));
    }

    [Fact]
    public void Each_kind_of_change_emits_only_its_own_event()
    {
        var history = Added(Input());
        Assert.IsType<SubscriptionDetailsChanged>(Assert.Single(SubscriptionDecisions.UpdateDetails(history, new DetailsUpdate("StreamCo 2", null, null, "DKK", new DateOnly(2024, 3, 1), null), Now)));
        Assert.IsType<SubscriptionDetailsChanged>(Assert.Single(SubscriptionDecisions.UpdateDetails(history, new DetailsUpdate("StreamCo", "Streaming", null, "DKK", new DateOnly(2024, 3, 1), null), Now)));
        Assert.IsType<SubscriptionCurrencyCorrected>(Assert.Single(SubscriptionDecisions.UpdateDetails(history, new DetailsUpdate("StreamCo", null, null, "EUR", new DateOnly(2024, 3, 1), null), Now)));
        var all = SubscriptionDecisions.UpdateDetails(history, new DetailsUpdate("X", "Y", "Z", "EUR", new DateOnly(2024, 1, 1), new DateOnly(2026, 1, 1)), Now);
        Assert.Equal([typeof(SubscriptionDetailsChanged), typeof(SubscriptionCurrencyCorrected), typeof(SubscriptionPeriodChanged)], all.Select(e => e.GetType()));
    }

    [Fact]
    public void D_11_currency_correction_is_judged_by_value()
    {
        var history = Added(Input(amount: "129.00"));
        var events = SubscriptionDecisions.UpdateDetails(history, new DetailsUpdate("StreamCo", null, null, "JPY", new DateOnly(2024, 3, 1), null), Now);
        Assert.IsType<SubscriptionCurrencyCorrected>(Assert.Single(events));
        var state = SubscriptionState.Rehydrate(history.Concat(events));
        Assert.Equal("129", Money.FormatAmount(state.Entries[0].Amount, 0));

        var half = Added(Input(amount: "129.50"));
        Assert.Equal("currency", Key(() => SubscriptionDecisions.UpdateDetails(half, new DetailsUpdate("StreamCo", null, null, "JPY", new DateOnly(2024, 3, 1), null), Now)));
    }

    [Fact]
    public void A_rejected_change_leaves_the_state_untouched()
    {
        var history = FilmPlus();
        var before = SubscriptionState.Rehydrate(history);
        Assert.Throws<SubscriptionValidationException>(() => SubscriptionDecisions.CorrectPriceEntry(history, "e2", Entry("2023-11-01", "69.00"), Now));
        Assert.Throws<SubscriptionValidationException>(() => SubscriptionDecisions.UpdateDetails(history, new DetailsUpdate("FilmPlus", null, null, "DKK", new DateOnly(2023, 1, 1), null), Now));
        var after = SubscriptionState.Rehydrate(history);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
        Assert.Equal(3, after.Entries.Count);
    }

    // ---- price entries -----------------------------------------------------------------------------

    [Fact]
    public void Price_entry_date_rules_name_effectiveFrom()
    {
        var history = Added(Input(end: "2026-12-31"));
        Assert.Equal("effectiveFrom", Key(() => SubscriptionDecisions.AddPriceEntry(history, Entry("2024-03-01", "1.00"), "e2", Now)));
        Assert.Equal("effectiveFrom", Key(() => SubscriptionDecisions.AddPriceEntry(history, Entry("2023-01-01", "1.00"), "e2", Now)));
        Assert.Equal("effectiveFrom", Key(() => SubscriptionDecisions.AddPriceEntry(history, Entry("2027-01-01", "1.00"), "e2", Now)));
        Apply(history, SubscriptionDecisions.AddPriceEntry(history, Entry("2025-01-01", "149.00"), "e2", Now));
        Assert.Equal("effectiveFrom", Key(() => SubscriptionDecisions.AddPriceEntry(history, Entry("2025-01-01", "150.00"), "e3", Now)));
        Assert.IsType<SubscriptionPriceEntryAdded>(Assert.Single(SubscriptionDecisions.AddPriceEntry(history, Entry("2026-12-31", "150.00"), "e3", Now)));
    }

    [Fact]
    public void Price_entry_precision_is_checked_against_the_subscription_currency_in_the_invariant_step()
    {
        var dkk = Added(Input());
        var change = Entry("2025-01-01", "99.955");   // shape passes: three fraction digits
        Assert.Equal("amount", Key(() => SubscriptionDecisions.AddPriceEntry(dkk, change, "e2", Now)));
        var jpy = Added(Input(currency: "JPY", amount: "1200"));
        Assert.Equal("amount", Key(() => SubscriptionDecisions.AddPriceEntry(jpy, Entry("2025-01-01", "1200.5"), "e2", Now)));
        Assert.Equal("amount", Key(() => SubscriptionDecisions.CorrectPriceEntry(jpy, "e1", Entry("2024-03-01", "1200.5"), Now)));
    }

    [Fact]
    public void The_initial_entry_cannot_be_removed_or_moved_off_the_start_date()
    {
        var history = FilmPlus();
        Assert.Equal("entryId", Key(() => SubscriptionDecisions.RemovePriceEntry(history, "e1", Now)));
        Assert.Equal("effectiveFrom", Key(() => SubscriptionDecisions.CorrectPriceEntry(history, "e1", Entry("2021-02-01", "59.00"), Now)));
    }

    [Fact]
    public void Unknown_entries_and_missing_or_removed_subscriptions_are_not_found()
    {
        var history = FilmPlus();
        Assert.Throws<SubscriptionNotFoundException>(() => SubscriptionDecisions.RemovePriceEntry(history, "nope", Now));
        Assert.Throws<SubscriptionNotFoundException>(() => SubscriptionDecisions.CorrectPriceEntry(history, "nope", Entry("2025-01-01", "1.00"), Now));
        Assert.Throws<SubscriptionNotFoundException>(() => SubscriptionDecisions.Remove([], Now));
        Apply(history, SubscriptionDecisions.Remove(history, Now));
        Assert.Throws<SubscriptionNotFoundException>(() => SubscriptionDecisions.Remove(history, Now));
        Assert.Throws<SubscriptionNotFoundException>(() => SubscriptionDecisions.AddPriceEntry(history, Entry("2030-01-01", "1.00"), "e9", Now));
        Assert.Throws<SubscriptionNotFoundException>(() => SubscriptionDecisions.UpdateDetails(history, new DetailsUpdate("x", null, null, "DKK", new DateOnly(2021, 1, 1), null), Now));
    }

    [Fact]
    public void A_correction_that_changes_nothing_is_a_no_op()
    {
        var history = FilmPlus();
        Assert.Empty(SubscriptionDecisions.CorrectPriceEntry(history, "e2", Entry("2022-06-01", "69.00"), Now));
        Assert.Empty(SubscriptionDecisions.CorrectPriceEntry(history, "e2", Entry("2022-06-01", "69"), Now));
    }

    // ---- BR-014 successful price-entry changes on FilmPlus ------------------------------------------

    private static IReadOnlyList<HistoryRow> Rows(List<object> history) => SubscriptionCalculator.History(SubscriptionState.Rehydrate(history), Today);

    [Fact]
    public void BR_014_1_correcting_the_initial_entry_amount()
    {
        var history = FilmPlus();
        Apply(history, SubscriptionDecisions.CorrectPriceEntry(history, "e1", Entry("2021-01-01", "49.00"), Now));
        var rows = Rows(history);
        Assert.Equal(new PriceChange(20.00m, 40.8m), rows[1].Change);
        Assert.Equal(new PriceChange(10.00m, 14.5m), rows[2].Change);
    }

    [Fact]
    public void BR_014_2_correcting_a_non_initial_amount_keeping_its_date()
    {
        var history = FilmPlus();
        Apply(history, SubscriptionDecisions.CorrectPriceEntry(history, "e2", Entry("2022-06-01", "65.00"), Now));
        var rows = Rows(history);
        Assert.Equal(new PriceChange(6.00m, 10.2m), rows[1].Change);
        Assert.Equal(new PriceChange(14.00m, 21.5m), rows[2].Change);
    }

    [Fact]
    public void BR_014_3_moving_an_entry_resorts_the_history()
    {
        var history = FilmPlus();
        Apply(history, SubscriptionDecisions.CorrectPriceEntry(history, "e2", Entry("2024-01-01", "69.00"), Now));
        var rows = Rows(history);
        Assert.Equal(["e1", "e3", "e2"], rows.Select(r => r.Entry.EntryId));
        Assert.Equal(new DateOnly(2023, 10, 31), rows[0].EffectiveTo);
        Assert.Equal(new DateOnly(2023, 12, 31), rows[1].EffectiveTo);
        Assert.Equal(new PriceChange(20.00m, 33.9m), rows[1].Change);
        Assert.True(rows[2].Current);
        Assert.Equal(new PriceChange(-10.00m, -12.7m), rows[2].Change);
    }

    [Fact]
    public void BR_014_4_removing_a_non_initial_entry()
    {
        var history = FilmPlus();
        Apply(history, SubscriptionDecisions.RemovePriceEntry(history, "e2", Now));
        var rows = Rows(history);
        Assert.Equal(2, rows.Count);
        Assert.Equal(new DateOnly(2023, 10, 31), rows[0].EffectiveTo);
        Assert.Equal(new PriceChange(20.00m, 33.9m), rows[1].Change);
    }

    [Fact]
    public void BR_014_5_moving_an_entry_onto_another_entrys_date_is_rejected()
    {
        var history = FilmPlus();
        Assert.Equal("effectiveFrom", Key(() => SubscriptionDecisions.CorrectPriceEntry(history, "e2", Entry("2023-11-01", "69.00"), Now)));
        Assert.Equal(3, SubscriptionState.Rehydrate(history).Entries.Count);
    }

    [Fact]
    public void BR_014_6_correcting_the_cadence_keeping_the_date()
    {
        var history = FilmPlus();
        Apply(history, SubscriptionDecisions.CorrectPriceEntry(history, "e2", Entry("2022-06-01", "69.00", "month", 3), Now));
        var rows = Rows(history);
        Assert.Equal(23.00m, rows[1].NormalizedMonthly);
        Assert.Equal(new PriceChange(-36.00m, -61.0m), rows[1].Change);
        Assert.Equal(new PriceChange(56.00m, 243.5m), rows[2].Change);
    }

    // ---- state -------------------------------------------------------------------------------------

    [Fact]
    public void Rehydrate_counts_one_version_per_event_and_rejects_malformed_streams()
    {
        var history = FilmPlus();
        Assert.Equal(3, SubscriptionState.Rehydrate(history).Version);
        Assert.Throws<InvalidOperationException>(() => SubscriptionState.Rehydrate([.. history, .. Added(Input())]));
        Assert.Throws<InvalidOperationException>(() => SubscriptionState.Rehydrate([new SubscriptionRemoved(Now)]));
        Assert.Throws<InvalidOperationException>(() => SubscriptionState.Rehydrate([]));
    }

    [Fact]
    public void Removal_marks_the_state_and_deletes_nothing()
    {
        var history = FilmPlus();
        Apply(history, SubscriptionDecisions.Remove(history, Now));
        var state = SubscriptionState.Rehydrate(history);
        Assert.True(state.Removed);
        Assert.Equal(Now.UtcDateTime, state.RemovedAt!.Value.UtcDateTime);
        Assert.Equal(3, state.Entries.Count);
    }
}

public sealed class SubscriptionCalculatorTests
{
    private static readonly DateOnly Today = SubscriptionDecisionTests.Today;

    private static SubscriptionState State(List<object> history, string id = "")
    {
        var state = SubscriptionState.Rehydrate(history);
        state.Id = id;
        return state;
    }

    private static List<object> Sub(string name, string currency, string amount, string unit, int count, string start, string? end = null, DateTimeOffset? at = null)
    {
        var added = SubscriptionDecisionTests.Added(SubscriptionDecisionTests.Input(name, currency, amount, unit, count, start, end));
        if (at is { } t)
        {
            added[0] = ((SubscriptionAdded)added[0]) with { RecordedAt = t };
        }

        return added;
    }

    [Fact]
    public void EX_007_history_changes_and_price_in_effect()
    {
        var state = State(SubscriptionDecisionTests.FilmPlus());
        var rows = SubscriptionCalculator.History(state, Today);
        Assert.Equal([59.00m, 69.00m, 79.00m], rows.Select(r => r.Entry.Amount));
        Assert.Null(rows[0].Change);
        Assert.Equal(new PriceChange(10.00m, 16.9m), rows[1].Change);
        Assert.Equal(new PriceChange(10.00m, 14.5m), rows[2].Change);
        Assert.Equal([false, false, true], rows.Select(r => r.Current));
        Assert.Equal([true, false, false], rows.Select(r => r.Initial));
        Assert.Equal(new DateOnly(2022, 5, 31), rows[0].EffectiveTo);
        Assert.Equal(new DateOnly(2023, 10, 31), rows[1].EffectiveTo);
        Assert.Null(rows[2].EffectiveTo);
        Assert.Equal(59.00m, SubscriptionCalculator.PriceInEffect(state, new DateOnly(2022, 5, 31))!.Amount);
        Assert.Equal(69.00m, SubscriptionCalculator.PriceInEffect(state, new DateOnly(2022, 6, 1))!.Amount);
        Assert.Null(SubscriptionCalculator.PriceInEffect(state, new DateOnly(2020, 12, 31)));
        Assert.Equal(79.00m, SubscriptionCalculator.CurrentPrice(state, Today).Amount);
    }

    [Fact]
    public void EX_008_cadence_switch()
    {
        var history = SubscriptionDecisionTests.Added(SubscriptionDecisionTests.Input("SeriesHub", amount: "99.00", start: "2025-01-01"));
        SubscriptionDecisionTests.Apply(history, SubscriptionDecisions.AddPriceEntry(history, SubscriptionDecisionTests.Entry("2026-03-01", "999.00", "year"), "e2", SubscriptionDecisionTests.Now));
        var state = State(history);
        var rows = SubscriptionCalculator.History(state, Today);
        Assert.Equal(83.25m, rows[1].NormalizedMonthly);
        Assert.Equal(999.00m, rows[1].NormalizedYearly);
        Assert.Equal(new PriceChange(-15.75m, -15.9m), rows[1].Change);
        Assert.Equal(2, rows.Count);
        Assert.Equal(999.00m, SubscriptionCalculator.CurrentPrice(state, Today).Amount);
    }

    [Fact]
    public void EX_009_upcoming_entry_becomes_current_once_its_date_passes()
    {
        var history = Sub("StreamCo", "DKK", "129.00", "month", 1, "2024-03-01");
        SubscriptionDecisionTests.Apply(history, SubscriptionDecisions.AddPriceEntry(history, SubscriptionDecisionTests.Entry("2027-01-01", "149.00"), "e2", SubscriptionDecisionTests.Now));
        var state = State(history);

        var before = SubscriptionCalculator.History(state, new DateOnly(2026, 12, 31));
        Assert.Equal([true, false], before.Select(r => r.Current));
        Assert.Equal([false, true], before.Select(r => r.Upcoming));
        Assert.True(SubscriptionCalculator.UpcomingPriceChange(state, new DateOnly(2026, 12, 31)));
        Assert.Equal(129.00m, Assert.Single(SubscriptionCalculator.Totals([state], new DateOnly(2026, 12, 31))).Monthly);

        var after = SubscriptionCalculator.History(state, new DateOnly(2027, 1, 1));
        Assert.Equal([false, true], after.Select(r => r.Current));
        Assert.Equal([false, false], after.Select(r => r.Upcoming));
        Assert.False(SubscriptionCalculator.UpcomingPriceChange(state, new DateOnly(2027, 1, 1)));
        Assert.Equal(149.00m, Assert.Single(SubscriptionCalculator.Totals([state], new DateOnly(2027, 1, 1))).Monthly);
    }

    [Fact]
    public void EX_010_ended_subscription_keeps_its_last_price_and_leaves_the_totals()
    {
        var state = State(Sub("SportsLive", "DKK", "149.00", "month", 1, "2023-01-01", "2026-06-30"));
        Assert.Equal(SubscriptionStatus.Ended, SubscriptionCalculator.StatusOn(state, Today));
        Assert.Equal(149.00m, SubscriptionCalculator.CurrentPrice(state, Today).Amount);
        Assert.Empty(SubscriptionCalculator.Totals([state], Today));
        Assert.False(SubscriptionCalculator.UpcomingPriceChange(state, Today));
        Assert.True(Assert.Single(SubscriptionCalculator.History(state, Today)).Current);
    }

    [Fact]
    public void EX_011_not_started_subscription_and_upcoming_price_change()
    {
        var history = Sub("KidsClub", "DKK", "60.00", "month", 1, "2026-12-01");
        var state = State(history);
        Assert.Equal(SubscriptionStatus.NotStarted, SubscriptionCalculator.StatusOn(state, Today));
        Assert.Equal(60.00m, SubscriptionCalculator.CurrentPrice(state, Today).Amount);
        Assert.Empty(SubscriptionCalculator.Totals([state], Today));
        Assert.False(SubscriptionCalculator.UpcomingPriceChange(state, Today));
        var only = Assert.Single(SubscriptionCalculator.History(state, Today));
        Assert.True(only.Current);
        Assert.True(only.Upcoming);

        SubscriptionDecisionTests.Apply(history, SubscriptionDecisions.AddPriceEntry(history, SubscriptionDecisionTests.Entry("2027-01-01", "70.00"), "e2", SubscriptionDecisionTests.Now));
        Assert.True(SubscriptionCalculator.UpcomingPriceChange(State(history), Today));
    }

    [Fact]
    public void EX_012_the_end_date_is_inclusive()
    {
        var active = State(Sub("A", "DKK", "10.00", "month", 1, "2024-01-01", "2026-11-13"));
        var ended = State(Sub("B", "DKK", "10.00", "month", 1, "2024-01-01", "2026-11-12"));
        Assert.Equal(SubscriptionStatus.Active, SubscriptionCalculator.StatusOn(active, Today));
        Assert.Equal(SubscriptionStatus.Ended, SubscriptionCalculator.StatusOn(ended, Today));
        Assert.Equal(1, Assert.Single(SubscriptionCalculator.Totals([active, ended], Today)).ActiveCount);
        Assert.Equal("not-started", SubscriptionStatus.NotStarted.ToWire());
        Assert.Equal("active", SubscriptionStatus.Active.ToWire());
        Assert.Equal("ended", SubscriptionStatus.Ended.ToWire());
    }

    [Fact]
    public void EX_005_totals_are_per_currency_sorted_and_never_combined()
    {
        var subs = new[]
        {
            State(Sub("StreamCo", "DKK", "129.00", "month", 1, "2024-03-01")),
            State(Sub("NewsDaily", "DKK", "45.00", "week", 1, "2024-03-01")),
            State(Sub("VideoTube", "EUR", "11.99", "month", 1, "2024-03-01")),
            State(Sub("AppSuite", "USD", "4.99", "month", 1, "2024-03-01")),
        };
        var totals = SubscriptionCalculator.Totals(subs, Today);
        Assert.Equal(["DKK", "EUR", "USD"], totals.Select(t => t.Currency));
        Assert.Equal(new CurrencyTotal("DKK", 2, 324.00m, 3888.00m, 2), totals[0]);
        Assert.Equal(new CurrencyTotal("EUR", 2, 11.99m, 143.88m, 1), totals[1]);
        Assert.Equal(new CurrencyTotal("USD", 2, 4.99m, 59.88m, 1), totals[2]);
    }

    [Fact]
    public void EX_006_totals_round_once()
    {
        var subs = new[] { State(Sub("A", "DKK", "1490.00", "year", 1, "2024-01-01")), State(Sub("B", "DKK", "1490.00", "year", 1, "2024-01-01")) };
        var total = Assert.Single(SubscriptionCalculator.Totals(subs, Today));
        Assert.Equal(248.33m, total.Monthly);
        Assert.Equal(2980.00m, total.Yearly);
        Assert.Equal(124.17m, SubscriptionCalculator.NormalizedMonthly(subs[0].Entries[0], 2));
    }

    [Fact]
    public void Removed_subscriptions_are_left_out_of_totals_and_default_currency()
    {
        var history = Sub("Gone", "EUR", "10.00", "month", 1, "2024-01-01");
        SubscriptionDecisionTests.Apply(history, SubscriptionDecisions.Remove(history, SubscriptionDecisionTests.Now));
        var gone = State(history);
        Assert.Empty(SubscriptionCalculator.Totals([gone], Today));
        Assert.Null(SubscriptionCalculator.DefaultCurrency([gone]));
        Assert.Null(SubscriptionCalculator.DefaultCurrency([]));
    }

    [Fact]
    public void Zero_price_omits_the_percentage_but_keeps_the_amount_change()
    {
        var history = Sub("Free trial", "DKK", "0.00", "month", 1, "2024-01-01");
        SubscriptionDecisionTests.Apply(history, SubscriptionDecisions.AddPriceEntry(history, SubscriptionDecisionTests.Entry("2024-02-01", "10.00"), "e2", SubscriptionDecisionTests.Now));
        var rows = SubscriptionCalculator.History(State(history), Today);
        Assert.Equal(new PriceChange(10.00m, null), rows[1].Change);
    }

    [Fact]
    public void A_non_zero_change_between_two_prices_that_display_the_same()
    {
        var history = Sub("Odd", "DKK", "1.00", "month", 1, "2024-01-01");
        SubscriptionDecisionTests.Apply(history, SubscriptionDecisions.AddPriceEntry(history, SubscriptionDecisionTests.Entry("2024-02-01", "11.94", "year"), "e2", SubscriptionDecisionTests.Now));
        var rows = SubscriptionCalculator.History(State(history), Today);
        Assert.Equal(1.00m, rows[0].NormalizedMonthly);
        Assert.Equal(1.00m, rows[1].NormalizedMonthly);
        Assert.Equal(-0.01m, rows[1].Change!.Amount);
    }

    [Fact]
    public void A_negative_change_that_rounds_to_zero_formats_without_a_minus_sign()
    {
        var history = Sub("Flat", "DKK", "100.00", "month", 1, "2024-01-01");
        SubscriptionDecisionTests.Apply(history, SubscriptionDecisions.AddPriceEntry(history, SubscriptionDecisionTests.Entry("2024-02-01", "1199.95", "year"), "e2", SubscriptionDecisionTests.Now));
        var change = SubscriptionCalculator.History(State(history), Today)[1].Change!;
        Assert.Equal("0.00", Money.FormatAmount(change.Amount, 2));
        Assert.Equal("0.0", Money.FormatAmount(change.Percent!.Value, 1));
    }

    [Fact]
    public void Default_currency_is_the_latest_added_and_ties_go_to_the_highest_id()
    {
        var t0 = DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture);
        var first = State(Sub("A", "DKK", "1.00", "month", 1, "2024-01-01", at: t0), "aaa");
        var second = State(Sub("B", "EUR", "1.00", "month", 1, "2024-01-01", at: t0.AddSeconds(5)), "bbb");
        Assert.Equal("EUR", SubscriptionCalculator.DefaultCurrency([first, second]));
        Assert.Equal("EUR", SubscriptionCalculator.DefaultCurrency([second, first]));

        var tieLow = State(Sub("A", "DKK", "1.00", "month", 1, "2024-01-01", at: t0), "0001");
        var tieHigh = State(Sub("B", "USD", "1.00", "month", 1, "2024-01-01", at: t0), "0002");
        Assert.Equal("USD", SubscriptionCalculator.DefaultCurrency([tieLow, tieHigh]));
        Assert.Equal("USD", SubscriptionCalculator.DefaultCurrency([tieHigh, tieLow]));
    }

    [Fact]
    public void List_order_is_name_then_start_date_then_id()
    {
        var a2 = State(Sub("Alpha", "DKK", "1.00", "month", 1, "2025-01-01"), "02");
        var a1 = State(Sub("Alpha", "DKK", "1.00", "month", 1, "2025-01-01"), "01");
        var aEarly = State(Sub("Alpha", "DKK", "1.00", "month", 1, "2024-01-01"), "09");
        var beta = State(Sub("Beta", "DKK", "1.00", "month", 1, "2020-01-01"), "00");
        var upper = State(Sub("alpha", "DKK", "1.00", "month", 1, "2020-01-01"), "10");
        Assert.Equal(["09", "01", "02", "00", "10"], SubscriptionCalculator.ListOrder([beta, a2, a1, aEarly, upper]).Select(s => s.Id));
    }

    [Fact]
    public void Minor_unit_values_follow_the_subscription_currency()
    {
        var jpy = State(Sub("J", "JPY", "1490", "year", 1, "2024-01-01"));
        Assert.Equal(124m, SubscriptionCalculator.NormalizedMonthly(jpy.Entries[0], SubscriptionCalculator.MinorUnitOf(jpy)));
        var kwd = State(Sub("K", "KWD", "1.234", "month", 1, "2024-01-01"));
        Assert.Equal(14.808m, SubscriptionCalculator.NormalizedYearly(kwd.Entries[0], SubscriptionCalculator.MinorUnitOf(kwd)));
    }

    [Fact]
    public void Today_is_the_utc_date()
    {
        Assert.Equal(new DateOnly(2026, 11, 13), SubscriptionCalculator.Today(new FixedClock(new DateTimeOffset(2026, 11, 13, 23, 59, 0, TimeSpan.Zero))));
        Assert.Equal(new DateOnly(2026, 11, 13), SubscriptionCalculator.Today(new FixedClock(new DateTimeOffset(2026, 11, 14, 0, 30, 0, TimeSpan.FromHours(2)))));
    }
}
