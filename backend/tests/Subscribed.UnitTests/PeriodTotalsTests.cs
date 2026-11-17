using System.Globalization;
using Subscribed.Subscriptions.Domain;
using Xunit;

namespace Subscribed.UnitTests;

public sealed class ReportPeriodTests
{
    private static readonly DateOnly Today = new(2026, 11, 13);

    private static ReportPeriod Parse(string wire, DateOnly today)
    {
        Assert.True(ReportPeriod.TryParse(wire, today, out var period), wire);
        return period;
    }

    [Theory]
    [InlineData("year-2026", 2026)]
    [InlineData("year-1900", 1900)]
    [InlineData("year-9999", 9999)]
    public void A_calendar_year_spans_january_first_to_december_thirty_first(string wire, int year)
    {
        var period = Parse(wire, Today);
        Assert.Equal(ReportPeriodKind.CalendarYear, period.Kind);
        Assert.Equal(year, period.Year);
        Assert.Equal(new DateOnly(year, 1, 1), period.From);
        Assert.Equal(new DateOnly(year, 12, 31), period.To);
        Assert.Equal(wire, period.ToWire());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("year-0")]
    [InlineData("year-0000")]
    [InlineData("year-1899")]
    [InlineData("year-899")]
    [InlineData("year-10000")]
    [InlineData("year-99999")]
    [InlineData("year-2026 ")]
    [InlineData(" year-2026")]
    [InlineData("Year-2026")]
    [InlineData("year-+026")]
    [InlineData("year--026")]
    [InlineData("year-20x6")]
    [InlineData("year-２０２６")]
    [InlineData("Last-12-months")]
    [InlineData("last-12-months ")]
    [InlineData("next-12-month")]
    public void Anything_else_does_not_parse(string? wire)
    {
        Assert.False(ReportPeriod.TryParse(wire, Today, out var fallback));
        Assert.Equal(ReportPeriod.CurrentYear(Today), fallback);
    }

    [Theory]
    // today, last from, next to
    [InlineData("2024-02-29", "2023-03-01", "2025-02-27")]
    [InlineData("2026-01-31", "2025-02-01", "2027-01-30")]
    [InlineData("2026-03-31", "2025-04-01", "2027-03-30")]
    [InlineData("2026-12-31", "2026-01-01", "2027-12-30")]
    [InlineData("2026-11-13", "2025-11-14", "2027-11-12")]
    public void The_rolling_windows_are_pinned(string today, string lastFrom, string nextTo)
    {
        var day = DateOnly.Parse(today, CultureInfo.InvariantCulture);
        var last = Parse("last-12-months", day);
        Assert.Equal(ReportPeriodKind.Last12Months, last.Kind);
        Assert.Null(last.Year);
        Assert.Equal(DateOnly.Parse(lastFrom, CultureInfo.InvariantCulture), last.From);
        Assert.Equal(day, last.To);
        Assert.Equal("last-12-months", last.ToWire());

        var next = Parse("next-12-months", day);
        Assert.Equal(ReportPeriodKind.Next12Months, next.Kind);
        Assert.Equal(day, next.From);
        Assert.Equal(DateOnly.Parse(nextTo, CultureInfo.InvariantCulture), next.To);
        Assert.Equal("next-12-months", next.ToWire());
    }

    [Fact]
    public void On_the_leap_day_one_window_is_366_days_and_the_other_365()
    {
        var day = new DateOnly(2024, 2, 29);
        var last = Parse("last-12-months", day);
        var next = Parse("next-12-months", day);
        Assert.Equal(366, last.To.DayNumber - last.From.DayNumber + 1);
        Assert.Equal(365, next.To.DayNumber - next.From.DayNumber + 1);
    }

    [Fact]
    public void The_default_period_is_the_current_calendar_year()
    {
        var period = ReportPeriod.CurrentYear(Today);
        Assert.Equal("year-2026", period.ToWire());
    }

    [Fact]
    public void Years_with_no_subscription_are_the_current_and_next_year()
    {
        Assert.Equal([2026, 2027], ReportPeriod.Years([], Today));
    }

    [Fact]
    public void Years_start_at_the_earliest_start_of_a_non_removed_subscription()
    {
        var early = Sub("2023-05-01");
        var late = Sub("2026-02-01");
        var removed = Sub("2019-01-01");
        removed.Removed = true;
        var future = Sub("2031-01-01");

        Assert.Equal([2023, 2024, 2025, 2026, 2027], ReportPeriod.Years([late, early, removed], Today));
        Assert.Equal([2026, 2027], ReportPeriod.Years([future], Today));
    }

    private static SubscriptionState Sub(string start) =>
        SubscriptionState.Rehydrate(SubscriptionDecisionTests.Added(SubscriptionDecisionTests.Input(start: start)));
}

public sealed class PeriodCostTests
{
    private static readonly DateOnly Today = SubscriptionDecisionTests.Today;
    private static readonly ReportPeriod Year2026 = ReportPeriod.ForYear(2026);

    private static (string Unit, int Count)[] Cadences { get; } =
        [(Cadence.Week, 1), (Cadence.Week, 2), (Cadence.Month, 1), (Cadence.Month, 3), (Cadence.Year, 1), (Cadence.Year, 2)];

    private static DateOnly D(string text) => DateOnly.Parse(text, CultureInfo.InvariantCulture);

    private static SubscriptionState Build(
        DateOnly start,
        DateOnly? end,
        decimal amount,
        string unit = Cadence.Month,
        int count = 1,
        params (DateOnly From, decimal Amount)[] changes)
    {
        var state = new SubscriptionState
        {
            Id = "subscription-x",
            Currency = "DKK",
            StartDate = start,
            EndDate = end,
            InitialEntryId = "e0",
            Entries = [new PriceEntryState { EntryId = "e0", EffectiveFrom = start, Amount = amount, CadenceUnit = unit, CadenceCount = count }],
        };
        var n = 1;
        foreach (var (from, price) in changes)
        {
            state.Entries.Add(new PriceEntryState { EntryId = "e" + n++, EffectiveFrom = from, Amount = price, CadenceUnit = unit, CadenceCount = count });
        }

        return state;
    }

    private static PausePeriod Pause(string from, string? to, string id = "p") => new(id, D(from), to is null ? null : D(to));

    private static PeriodCharge Cost(SubscriptionState state, ReportPeriod period, params PausePeriod[] pauses) =>
        SubscriptionCalculator.PeriodCost(state, period, pauses);

    private static ReportPeriod Window(string from, string to) => new(ReportPeriodKind.CalendarYear, null, D(from), D(to));

    // ---- worked examples -------------------------------------------------------------------------

    [Fact]
    public void Acceptance_2_a_price_change_in_the_year_gives_1320_99()
    {
        var state = Build(D("2026-01-01"), null, 100m, Cadence.Month, 1, (D("2026-07-01"), 120m));
        var charge = Cost(state, Year2026);

        // 1200 * 181 / 365 + 1440 * 184 / 365 = 482160 / 365
        Assert.Equal(new Fraction(482160, 365), charge.Cost);
        Assert.Equal(365, charge.ChargedDays);
        Assert.Equal(1320.99m, Money.RoundHalfAwayFromZero(charge.Cost, 2));
        Assert.Equal(1320.99m, Assert.Single(SubscriptionCalculator.Totals([state], Year2026, TestZones.Utc, Today)).Total);
    }

    [Fact]
    public void Acceptance_3_a_start_on_october_first_counts_only_october_to_december()
    {
        var charge = Cost(Build(D("2026-10-01"), null, 100m), Year2026);
        Assert.Equal(92, charge.ChargedDays);
        Assert.Equal(new Fraction(1200 * 92, 365), charge.Cost);
    }

    [Fact]
    public void Acceptance_3_an_end_on_march_thirty_first_counts_only_up_to_it_inclusive()
    {
        var charge = Cost(Build(D("2024-01-01"), D("2026-03-31"), 100m), Year2026);
        Assert.Equal(90, charge.ChargedDays);
        Assert.Equal(new Fraction(1200 * 90, 365), charge.Cost);
    }

    [Fact]
    public void A_pause_inside_the_period_removes_its_days_and_resume_is_exclusive()
    {
        var state = Build(D("2024-01-01"), null, 100m);
        var charge = Cost(state, Year2026, Pause("2026-03-01", "2026-04-01"));
        Assert.Equal(365 - 31, charge.ChargedDays);
        Assert.Equal(new Fraction(1200 * (365 - 31), 365), charge.Cost);

        // The resume date itself is charged again.
        var oneDay = Window("2026-04-01", "2026-04-01");
        Assert.Equal(1, Cost(state, oneDay, Pause("2026-03-01", "2026-04-01")).ChargedDays);
        Assert.Equal(0, Cost(state, Window("2026-03-31", "2026-03-31"), Pause("2026-03-01", "2026-04-01")).ChargedDays);
        Assert.Equal(0, Cost(state, Window("2026-03-01", "2026-03-01"), Pause("2026-03-01", "2026-04-01")).ChargedDays);
    }

    [Fact]
    public void A_pause_that_spans_a_price_change_is_not_charged_at_either_price()
    {
        var state = Build(D("2024-01-01"), null, 100m, Cadence.Month, 1, (D("2026-06-01"), 200m));
        var charge = Cost(state, Year2026, Pause("2026-05-01", "2026-07-01"));
        var before = new Fraction(1200 * 120, 365);   // Jan 1 - Apr 30
        var after = new Fraction(2400 * 184, 365);    // Jul 1 - Dec 31
        Assert.Equal(before + after, charge.Cost);
        Assert.Equal(120 + 184, charge.ChargedDays);
    }

    [Fact]
    public void Acceptance_4_an_open_pause_counts_zero_from_today_in_the_next_twelve_months()
    {
        var state = Build(D("2024-01-01"), null, 100m);
        Assert.True(ReportPeriod.TryParse("next-12-months", Today, out var next));
        var charge = Cost(state, next, Pause("2026-11-13", null));
        Assert.Equal(Fraction.Zero, charge.Cost);
        Assert.Equal(0, charge.ChargedDays);

        // The same open pause costs nothing from today on, but the days before it still count in a calendar year.
        var year = Cost(state, Year2026, Pause("2026-11-13", null));
        Assert.Equal(316, year.ChargedDays);
    }

    [Fact]
    public void A_pause_outside_the_start_and_end_dates_changes_nothing()
    {
        var state = Build(D("2026-03-01"), D("2026-09-30"), 100m);
        var plain = Cost(state, Year2026);
        var paused = Cost(state, Year2026, Pause("2025-12-01", "2026-03-01"), Pause("2026-10-01", null));
        Assert.Equal(plain, paused);
        Assert.Equal(214, plain.ChargedDays);
    }

    [Fact]
    public void A_scheduled_price_change_after_today_applies_only_in_the_next_twelve_months()
    {
        var state = Build(D("2024-01-01"), null, 100m, Cadence.Month, 1, (D("2027-01-01"), 150m));
        Assert.True(ReportPeriod.TryParse("last-12-months", Today, out var last));
        Assert.True(ReportPeriod.TryParse("next-12-months", Today, out var next));

        Assert.Equal(new Fraction(1200 * 365, 365), Cost(state, last).Cost);

        // Nov 13 - Dec 31 2026 is 49 days at 1200, Jan 1 - Nov 12 2027 is 316 days at 1800.
        Assert.Equal(new Fraction(1200 * 49 + 1800 * 316, 365), Cost(state, next).Cost);
    }

    [Fact]
    public void A_price_entry_on_the_end_date_charges_that_last_day_at_the_new_price()
    {
        var state = Build(D("2026-01-01"), D("2026-06-30"), 100m, Cadence.Month, 1, (D("2026-06-30"), 200m));
        var charge = Cost(state, Year2026);
        Assert.Equal(new Fraction(1200 * 180 + 2400, 365), charge.Cost);
        Assert.Equal(181, charge.ChargedDays);
    }

    [Fact]
    public void Single_day_and_zero_day_ranges()
    {
        var state = Build(D("2026-06-15"), D("2026-06-15"), 100m);
        Assert.Equal(new Fraction(1200, 365), Cost(state, Year2026).Cost);
        Assert.Equal(1, Cost(state, Year2026).ChargedDays);

        var before = Build(D("2026-06-16"), null, 100m);
        Assert.Equal(new PeriodCharge(Fraction.Zero, 0), Cost(before, Window("2026-06-01", "2026-06-15")));
        var after = Build(D("2024-01-01"), D("2026-05-31"), 100m);
        Assert.Equal(new PeriodCharge(Fraction.Zero, 0), Cost(after, Window("2026-06-01", "2026-06-15")));
    }

    [Fact]
    public void A_leap_year_uses_366_as_the_divisor_and_a_window_over_new_year_splits()
    {
        var state = Build(D("2020-01-01"), null, 100m);
        var leap = Cost(state, ReportPeriod.ForYear(2024));
        Assert.Equal(new Fraction(1200, 1), leap.Cost);
        Assert.Equal(366, leap.ChargedDays);

        var crossing = Cost(state, Window("2023-12-31", "2024-01-01"));
        Assert.Equal(new Fraction(1200, 365) + new Fraction(1200, 366), crossing.Cost);
        Assert.Equal(2, crossing.ChargedDays);
    }

    [Fact]
    public void The_extreme_years_do_not_overflow()
    {
        Assert.True(ReportPeriod.TryParse("year-1900", Today, out var first));
        var old = Build(D("1900-01-01"), null, 100m);
        Assert.Equal(new Fraction(1200, 1), Cost(old, first).Cost);
        Assert.Equal(new Fraction(1200 * 4, 365), Cost(old, Window("1900-12-28", "1900-12-31")).Cost);

        Assert.True(ReportPeriod.TryParse("year-9999", Today, out var last));
        var late = Build(D("9999-12-01"), null, 100m);
        var charge = Cost(late, last, Pause("9999-12-10", "9999-12-20"));
        Assert.Equal(31 - 10, charge.ChargedDays);
        Assert.Equal(new Fraction(1200 * 21, 365), charge.Cost);
        Assert.Equal(new PeriodCharge(Fraction.Zero, 0), Cost(Build(D("9999-12-31"), null, 100m), Window("9999-12-30", "9999-12-30")));
    }

    // ---- invariants ------------------------------------------------------------------------------

    [Fact]
    public void A_full_calendar_year_at_a_constant_price_is_exactly_the_yearly_cost_in_365_and_366_day_years()
    {
        foreach (var (unit, count) in Cadences)
        {
            var state = Build(D("2020-01-01"), null, 12.34m, unit, count);
            var yearly = new Cadence(unit, count).Yearly(12.34m);
            Assert.Equal(yearly, Cost(state, ReportPeriod.ForYear(2026)).Cost);
            Assert.Equal(yearly, Cost(state, ReportPeriod.ForYear(2024)).Cost);
        }
    }

    [Fact]
    public void Without_a_price_change_or_pause_the_result_does_not_depend_on_how_the_range_is_segmented()
    {
        foreach (var (unit, count) in Cadences)
        {
            var state = Build(D("2020-01-01"), null, 7.77m, unit, count);
            var whole = Cost(state, Window("2023-06-15", "2025-03-09"));
            var splits = new[] { "2023-06-16", "2023-12-31", "2024-01-01", "2024-02-29", "2024-12-31", "2025-01-01", "2025-03-09" };
            foreach (var split in splits)
            {
                var cut = D(split);
                var left = Cost(state, new ReportPeriod(ReportPeriodKind.CalendarYear, null, D("2023-06-15"), cut.AddDays(-1)));
                var right = Cost(state, new ReportPeriod(ReportPeriodKind.CalendarYear, null, cut, D("2025-03-09")));
                Assert.Equal(whole.Cost, left.Cost + right.Cost);
                Assert.Equal(whole.ChargedDays, left.ChargedDays + right.ChargedDays);
            }
        }
    }

    // ---- differential oracle ---------------------------------------------------------------------

    /// <summary>
    /// A naive per-day loop with its own price lookup (the last entry with <c>EffectiveFrom &lt;= day</c>, else the earliest), its own
    /// pause test and the day number of December 31 for the divisor. It never calls <c>PriceInEffect</c> or <c>IsPausedOn</c>.
    /// </summary>
    private static (Fraction Cost, int Days) Oracle(SubscriptionState state, ReportPeriod period, IReadOnlyList<PausePeriod> pauses)
    {
        var total = Fraction.Zero;
        var days = 0;
        var ordered = state.Entries.OrderBy(e => e.EffectiveFrom).ToList();
        for (var day = period.From; day <= period.To; day = day.AddDays(1))
        {
            if (day < state.StartDate || (state.EndDate is { } end && day > end))
            {
                continue;
            }

            var paused = false;
            foreach (var p in pauses)
            {
                if (day >= p.PauseDate && (p.ResumeDate is null || day < p.ResumeDate.Value))
                {
                    paused = true;
                }
            }

            if (paused)
            {
                continue;
            }

            var price = ordered[0];
            foreach (var entry in ordered)
            {
                if (entry.EffectiveFrom <= day)
                {
                    price = entry;
                }
            }

            total += new Cadence(price.CadenceUnit, price.CadenceCount).Yearly(price.Amount) / new DateOnly(day.Year, 12, 31).DayOfYear;
            days++;
        }

        return (total, days);
    }

    private sealed record Scenario(string Name, DateOnly Start, DateOnly? End, (DateOnly From, decimal Amount)[] Changes, PausePeriod[] Pauses);

    private static IEnumerable<Scenario> Scenarios(ReportPeriod p)
    {
        var f = p.From;
        var t = p.To;
        var jan1 = Enumerable.Range(f.Year + 1, 2).Select(y => new DateOnly(y, 1, 1)).FirstOrDefault(d => d > f && d <= t);
        if (jan1 == default)
        {
            jan1 = f.AddDays(5);
        }

        var early = f.AddDays(-400);
        PausePeriod P(DateOnly from, DateOnly? to, string id = "p") => new(id, from, to);

        yield return new("constant", early, null, [], []);
        yield return new("change-on-from", early, null, [(f, 20.25m)], []);
        yield return new("change-on-to", early, null, [(t, 20.25m)], []);
        yield return new("change-on-jan1", early, null, [(jan1, 20.25m)], []);
        yield return new("change-day-before-jan1", early, null, [(jan1.AddDays(-1), 20.25m)], []);
        yield return new("pause-resume-exclusive", early, null, [], [P(f.AddDays(30), f.AddDays(60))]);
        yield return new("change-at-pause-start", early, null, [(f.AddDays(30), 14.5m)], [P(f.AddDays(30), f.AddDays(60))]);
        yield return new("change-at-resume", early, null, [(f.AddDays(60), 14.5m)], [P(f.AddDays(30), f.AddDays(60))]);
        yield return new("pause-spans-change", early, null, [(f.AddDays(50), 14.5m)], [P(f.AddDays(20), f.AddDays(80))]);
        yield return new("open-pause", early, null, [], [P(f.AddDays(100), null)]);
        yield return new("pause-starts-before-period", early, null, [], [P(f.AddDays(-10), f.AddDays(5))]);
        yield return new("pause-covers-period", early, null, [], [P(f.AddDays(-1), t.AddDays(1))]);
        yield return new("adjacent-pauses", early, null, [], [P(f.AddDays(10), f.AddDays(20), "a"), P(f.AddDays(20), f.AddDays(30), "b")]);
        yield return new("start-inside", f.AddDays(10), null, [], []);
        yield return new("end-inside", early, t.AddDays(-10), [], []);
        yield return new("start-on-last-day", t, null, [], []);
        yield return new("end-on-first-day", early, f, [], []);
        yield return new("ended-before", early, f.AddDays(-1), [], []);
        yield return new("starts-after", t.AddDays(1), null, [], []);
        yield return new("change-on-end-date", early, t.AddDays(-5), [(t.AddDays(-5), 14.5m)], []);
        yield return new("pauses-outside-dates", f.AddDays(10), t.AddDays(-10), [], [P(f.AddDays(-30), f.AddDays(10), "a"), P(t.AddDays(-10), t.AddDays(20), "b")]);
        yield return new("many-changes", early, null, [(f.AddDays(7), 14.5m), (f.AddDays(40), 20.25m), (f.AddDays(200), 7.3m), (jan1, 9.1m)], [P(f.AddDays(41), f.AddDays(45))]);
    }

    public static TheoryData<string> PeriodKeys => new() { "year-2023", "year-2024", "year-2026", "L:2024-01-15", "N:2024-02-29", "L:2024-02-29", "N:2023-06-15", "L:2027-01-01" };

    private static ReportPeriod PeriodOf(string key)
    {
        if (key.StartsWith("L:", StringComparison.Ordinal))
        {
            Assert.True(ReportPeriod.TryParse("last-12-months", D(key[2..]), out var last));
            return last;
        }

        if (key.StartsWith("N:", StringComparison.Ordinal))
        {
            Assert.True(ReportPeriod.TryParse("next-12-months", D(key[2..]), out var next));
            return next;
        }

        Assert.True(ReportPeriod.TryParse(key, Today, out var year));
        return year;
    }

    [Theory]
    [MemberData(nameof(PeriodKeys))]
    public void PeriodCost_matches_a_per_day_oracle_over_the_scenario_matrix(string key)
    {
        var period = PeriodOf(key);
        var failures = new List<string>();
        var checkedCases = 0;
        foreach (var (unit, count) in Cadences)
        {
            foreach (var scenario in Scenarios(period))
            {
                // Two price entries never share a date (validation rejects it), so a scenario that lands two on one day keeps the first.
                var state = Build(scenario.Start, scenario.End, 9.99m, unit, count, [.. scenario.Changes.Where(c => c.From > scenario.Start).GroupBy(c => c.From).Select(g => g.First())]);
                var expected = Oracle(state, period, scenario.Pauses);
                var actual = SubscriptionCalculator.PeriodCost(state, period, scenario.Pauses);
                checkedCases++;
                if (expected.Cost != actual.Cost || expected.Days != actual.ChargedDays)
                {
                    failures.Add($"{key} {unit}x{count} {scenario.Name}: expected {expected.Cost} / {expected.Days} days, got {actual.Cost} / {actual.ChargedDays} days");
                }
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} of {checkedCases} scenarios differ:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    // ---- totals: counting and rounding -----------------------------------------------------------

    private static IReadOnlyList<CurrencyTotal> Totals(ReportPeriod period, params SubscriptionState[] states) =>
        SubscriptionCalculator.Totals(states, period, TestZones.Utc, Today);

    [Fact]
    public void ActiveCount_edges_follow_chargeable_days_in_the_period()
    {
        var june = Window("2026-06-01", "2026-06-30");

        var endedDayBefore = Build(D("2024-01-01"), D("2026-05-31"), 10m);
        var startsDayAfter = Build(D("2026-07-01"), null, 10m);
        var pausedWhole = Build(D("2024-01-01"), null, 10m);
        pausedWhole.PausePeriods.Add(new PausePeriodState { PeriodId = "p", PauseDate = D("2026-05-15"), ResumeDate = D("2026-07-02") });
        var oneDay = Build(D("2026-06-30"), null, 10m);
        var lastDay = Build(D("2024-01-01"), D("2026-06-01"), 10m);

        var total = Assert.Single(Totals(june, endedDayBefore, startsDayAfter, pausedWhole, oneDay, lastDay));
        Assert.Equal(2, total.ActiveCount);
        Assert.Equal(3, Assert.Single(Totals(june, endedDayBefore, pausedWhole, oneDay, lastDay, Build(D("2024-01-01"), null, 10m))).ActiveCount);
    }

    [Fact]
    public void A_paused_whole_period_contributes_zero()
    {
        var paused = Build(D("2024-01-01"), null, 10m);
        paused.PausePeriods.Add(new PausePeriodState { PeriodId = "p", PauseDate = D("2025-12-31") });
        var total = Assert.Single(Totals(Year2026, paused));
        Assert.Equal(0.00m, total.Total);
        Assert.Equal(0, total.ActiveCount);
    }

    [Fact]
    public void Rounding_happens_once_on_the_sum_not_per_subscription()
    {
        // Each is 31/365 of 1.00: 0.0849 rounds to 0.08, but the pair is 0.1699 and rounds to 0.17.
        var a = Build(D("2026-12-01"), null, 1.00m, Cadence.Year);
        var b = Build(D("2026-12-01"), null, 1.00m, Cadence.Year);
        Assert.Equal(0.08m, Money.RoundHalfAwayFromZero(Cost(a, Year2026).Cost, 2));
        var total = Assert.Single(Totals(Year2026, a, b));
        Assert.Equal(0.17m, total.Total);
        Assert.Equal(2, total.ActiveCount);
    }

    [Fact]
    public void MonthlyAverage_comes_from_the_unrounded_total()
    {
        // The exact total is 0.0588: rounded it is 0.06, and 0.06 / 12 = 0.005 would round up to 0.01. 0.0588 / 12 = 0.0049 is 0.00.
        var state = Build(D("2024-01-01"), null, 0.0588m, Cadence.Year);
        var total = Assert.Single(Totals(Year2026, state));
        Assert.Equal(0.06m, total.Total);
        Assert.Equal(0.00m, total.MonthlyAverage);
    }

    [Fact]
    public void Half_is_rounded_away_from_zero()
    {
        var state = Build(D("2024-01-01"), null, 0.005m, Cadence.Year);
        Assert.Equal(0.01m, Assert.Single(Totals(Year2026, state)).Total);
    }

    [Fact]
    public void A_zero_decimal_and_a_three_decimal_currency_round_to_their_minor_unit()
    {
        var yen = Build(D("2026-12-01"), null, 1200m);
        yen.Currency = "JPY";
        var jpy = Assert.Single(Totals(Year2026, yen));
        Assert.Equal(0, jpy.MinorUnit);
        Assert.Equal(1223m, jpy.Total);          // 14400 * 31 / 365 = 1223.01
        Assert.Equal(102m, jpy.MonthlyAverage);  // 1223.01 / 12 = 101.92

        var dinar = Build(D("2026-12-01"), null, 1.000m, Cadence.Year);
        dinar.Currency = "KWD";
        var kwd = Assert.Single(Totals(Year2026, dinar));
        Assert.Equal(3, kwd.MinorUnit);
        Assert.Equal(0.085m, kwd.Total);          // 31 / 365 = 0.08493
        Assert.Equal(0.007m, kwd.MonthlyAverage); // 0.08493 / 12 = 0.00708
    }

    [Fact]
    public void Currencies_stay_separate_and_removed_subscriptions_are_left_out()
    {
        var dkk = Build(D("2024-01-01"), null, 10m);
        var eur = Build(D("2024-01-01"), null, 10m);
        eur.Currency = "EUR";
        var gone = Build(D("2024-01-01"), null, 10m);
        gone.Removed = true;

        var totals = Totals(Year2026, eur, gone, dkk);
        Assert.Equal(["DKK", "EUR"], totals.Select(t => t.Currency));
        Assert.All(totals, t => Assert.Equal(1, t.ActiveCount));
        Assert.All(totals, t => Assert.Equal(120.00m, t.Total));
    }

    [Fact]
    public void Totals_resolve_legacy_pauses_with_the_household_zone_and_today()
    {
        var history = SubscriptionDecisionTests.Added(SubscriptionDecisionTests.Input(start: "2024-01-01", amount: "100.00"));
        history.WithPaused(new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero)).WithResumed(new DateTimeOffset(2026, 3, 20, 12, 0, 0, TimeSpan.Zero));
        var state = SubscriptionState.Rehydrate(history);

        var total = Assert.Single(SubscriptionCalculator.Totals([state], Year2026, TestZones.Copenhagen, Today));
        Assert.Equal(Money.RoundHalfAwayFromZero(new Fraction(1200 * 355, 365), 2), total.Total);
    }
}
