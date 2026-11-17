namespace Subscribed.Subscriptions.Domain;

public enum SubscriptionStatus
{
    NotStarted,
    Active,
    Ended,
    Paused,
}

public static class SubscriptionStatusExtensions
{
    /// <summary>kebab-case, like the session states.</summary>
    public static string ToWire(this SubscriptionStatus status) => status switch
    {
        SubscriptionStatus.NotStarted => "not-started",
        SubscriptionStatus.Active => "active",
        SubscriptionStatus.Ended => "ended",
        SubscriptionStatus.Paused => "paused",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}

/// <param name="Amount">Rounded to the currency's minor unit, half away from zero.</param>
/// <param name="Percent">Rounded to one decimal, or null when the previous monthly cost is zero (BR-018).</param>
public sealed record PriceChange(decimal Amount, decimal? Percent);

public sealed record HistoryRow(
    PriceEntryState Entry,
    bool Initial,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    decimal NormalizedMonthly,
    decimal NormalizedYearly,
    PriceChange? Change,
    bool Current,
    bool Upcoming);

/// <param name="Total">The period total, rounded once.</param>
/// <param name="MonthlyAverage">The unrounded period total divided by <see cref="ReportPeriod.Months"/>, rounded once.</param>
/// <param name="ActiveCount">Non-removed subscriptions with at least one chargeable day in the period (D8).</param>
public sealed record CurrencyTotal(string Currency, int MinorUnit, decimal Total, decimal MonthlyAverage, int ActiveCount);

/// <param name="Cost">The exact, unrounded cost of the period.</param>
/// <param name="ChargedDays">The number of chargeable days: inside start and end, outside every pause.</param>
public sealed record PeriodCharge(Fraction Cost, int ChargedDays);

/// <summary>Read-side calculations. Pure functions: nothing time-dependent is stored (BR-021).</summary>
public static class SubscriptionCalculator
{
    public static DateOnly Today(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        return DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
    }

    /// <summary>
    /// The end date is inclusive (BR-006). Dates are authoritative: <see cref="SubscriptionStatus.NotStarted"/> and
    /// <see cref="SubscriptionStatus.Ended"/> win over a pause, which only shows when the subscription would otherwise be active.
    /// <paramref name="day"/> is also the clamp date for pause periods derived from legacy events (D11), so callers pass today.
    /// </summary>
    public static SubscriptionStatus StatusOn(SubscriptionState state, DateOnly day, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(zone);
        if (day < state.StartDate)
        {
            return SubscriptionStatus.NotStarted;
        }

        if (IsEnded(state, day))
        {
            return SubscriptionStatus.Ended;
        }

        return IsPausedOn(state.ResolvePausePeriods(zone, day), day) ? SubscriptionStatus.Paused : SubscriptionStatus.Active;
    }

    /// <summary>True when the end date is before <paramref name="day"/>: the same rule <see cref="StatusOn"/> uses, with no zone needed.</summary>
    public static bool IsEnded(SubscriptionState state, DateOnly day)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.EndDate is { } end && day > end;
    }

    /// <summary>True when <paramref name="day"/> is inside <c>[pause, resume)</c> of any period; an open period has no end.</summary>
    public static bool IsPausedOn(IReadOnlyList<PausePeriod> periods, DateOnly day)
    {
        ArgumentNullException.ThrowIfNull(periods);
        foreach (var p in periods)
        {
            if (p.PauseDate <= day && (p.ResumeDate is not { } resume || day < resume))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The entry with the latest <c>effectiveFrom</c> on or before <paramref name="day"/>.</summary>
    public static PriceEntryState? PriceInEffect(SubscriptionState state, DateOnly day)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Entries.Where(e => e.EffectiveFrom <= day).MaxBy(e => e.EffectiveFrom);
    }

    /// <summary>The price in effect on today clamped into the subscription's own period (BR-016).</summary>
    public static PriceEntryState CurrentPrice(SubscriptionState state, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(state);
        var day = today < state.StartDate ? state.StartDate
            : state.EndDate is { } end && today > end ? end
            : today;
        return PriceInEffect(state, day) ?? state.Entries.MinBy(e => e.EffectiveFrom)!;
    }

    public static int MinorUnitOf(SubscriptionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Iso4217.TryGetMinorUnit(state.Currency, out var minor)
            ? minor
            : throw new InvalidOperationException($"Stored currency '{state.Currency}' is not in the ISO 4217 table.");
    }

    public static decimal NormalizedMonthly(PriceEntryState entry, int minorUnit)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Money.RoundHalfAwayFromZero(entry.Cadence.Monthly(entry.Amount), minorUnit);
    }

    public static decimal NormalizedYearly(PriceEntryState entry, int minorUnit)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Money.RoundHalfAwayFromZero(entry.Cadence.Yearly(entry.Amount), minorUnit);
    }

    /// <summary>Chronological history. Changes compare unrounded normalized monthly costs (BR-018).</summary>
    public static IReadOnlyList<HistoryRow> History(SubscriptionState state, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(state);
        var minor = MinorUnitOf(state);
        var sorted = state.Entries.OrderBy(e => e.EffectiveFrom).ToList();
        var current = CurrentPrice(state, today);
        var rows = new List<HistoryRow>(sorted.Count);
        for (var i = 0; i < sorted.Count; i++)
        {
            var entry = sorted[i];
            DateOnly? effectiveTo = i + 1 < sorted.Count ? sorted[i + 1].EffectiveFrom.AddDays(-1) : state.EndDate;
            PriceChange? change = null;
            if (i > 0)
            {
                var before = sorted[i - 1].Cadence.Monthly(sorted[i - 1].Amount);
                var after = entry.Cadence.Monthly(entry.Amount);
                var diff = after - before;
                change = new PriceChange(
                    Money.RoundHalfAwayFromZero(diff, minor),
                    before.IsZero ? null : Money.RoundHalfAwayFromZero(diff / before * 100, 1));
            }

            rows.Add(new HistoryRow(
                entry,
                string.Equals(entry.EntryId, state.InitialEntryId, StringComparison.Ordinal),
                entry.EffectiveFrom,
                effectiveTo,
                NormalizedMonthly(entry, minor),
                NormalizedYearly(entry, minor),
                change,
                ReferenceEquals(entry, current),
                entry.EffectiveFrom > today));
        }

        return rows;
    }

    /// <summary>True when a non-initial entry takes effect after today. A not-started subscription with only its initial entry has none.</summary>
    public static bool UpcomingPriceChange(SubscriptionState state, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Entries.Any(e => !string.Equals(e.EntryId, state.InitialEntryId, StringComparison.Ordinal) && e.EffectiveFrom > today);
    }

    /// <summary>
    /// The exact cost of one subscription over the period, day by day without a per-day loop. The range is clamped to the start and
    /// end dates (both inclusive) and split at every price change, pause boundary and Jan 1, so the price and the pause state are
    /// constant inside a segment and a segment never spans two calendar years. A segment costs
    /// <c>yearly * days / days-in-year</c> as one exact division; nothing is rounded here (R2.4).
    /// </summary>
    public static PeriodCharge PeriodCost(SubscriptionState state, ReportPeriod period, IReadOnlyList<PausePeriod> pauses)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(pauses);
        var from = period.From > state.StartDate ? period.From : state.StartDate;
        var to = state.EndDate is { } end && end < period.To ? end : period.To;
        if (from > to)
        {
            return new PeriodCharge(Fraction.Zero, 0);
        }

        var starts = new SortedSet<DateOnly> { from };
        foreach (var e in state.Entries)
        {
            AddBreakpoint(starts, e.EffectiveFrom, from, to);
        }

        foreach (var p in pauses)
        {
            AddBreakpoint(starts, p.PauseDate, from, to);
            if (p.ResumeDate is { } resume)
            {
                AddBreakpoint(starts, resume, from, to);
            }
        }

        for (var year = from.Year + 1; year <= to.Year; year++)
        {
            starts.Add(new DateOnly(year, 1, 1));
        }

        var ordered = starts.ToList();
        var cost = Fraction.Zero;
        var charged = 0;
        for (var i = 0; i < ordered.Count; i++)
        {
            var a = ordered[i];
            var b = i + 1 < ordered.Count ? ordered[i + 1].AddDays(-1) : to;
            if (IsPausedOn(pauses, a))
            {
                continue;
            }

            var price = PriceInEffect(state, a) ?? state.Entries.MinBy(x => x.EffectiveFrom)!;
            var days = b.DayNumber - a.DayNumber + 1;
            cost += price.Cadence.Yearly(price.Amount) * days / (DateTime.IsLeapYear(a.Year) ? 366 : 365);
            charged += days;
        }

        return new PeriodCharge(cost, charged);
    }

    private static void AddBreakpoint(SortedSet<DateOnly> starts, DateOnly day, DateOnly from, DateOnly to)
    {
        if (day > from && day <= to)
        {
            starts.Add(day);
        }
    }

    /// <summary>
    /// Per currency, over non-removed subscriptions (ended and not-yet-started ones included): the exact period costs are summed and
    /// rounded once, and the monthly average is that unrounded sum divided by twelve, rounded once (not the rounded total divided).
    /// A subscription is counted when it had at least one chargeable day in the period (D8). Sorted by code. Currencies are never
    /// combined. <paramref name="today"/> is the clamp date for pause periods derived from legacy events (D11).
    /// </summary>
    public static IReadOnlyList<CurrencyTotal> Totals(IEnumerable<SubscriptionState> subscriptions, ReportPeriod period, TimeZoneInfo zone, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);
        ArgumentNullException.ThrowIfNull(period);
        ArgumentNullException.ThrowIfNull(zone);
        return subscriptions
            .Where(s => !s.Removed)
            .GroupBy(s => s.Currency, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var minor = MinorUnitOf(g.First());
                var sum = Fraction.Zero;
                var active = 0;
                foreach (var s in g)
                {
                    var charge = PeriodCost(s, period, s.ResolvePausePeriods(zone, today));
                    sum += charge.Cost;
                    if (charge.ChargedDays > 0)
                    {
                        active++;
                    }
                }

                return new CurrencyTotal(g.Key, minor, Money.RoundHalfAwayFromZero(sum, minor), Money.RoundHalfAwayFromZero(sum / ReportPeriod.Months, minor), active);
            })
            .ToList();
    }

    /// <summary>The currency of the non-removed subscription with the latest <c>AddedAt</c>, ties broken by the highest id.</summary>
    public static string? DefaultCurrency(IEnumerable<SubscriptionState> subscriptions)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);
        return subscriptions
            .Where(s => !s.Removed)
            .OrderByDescending(s => s.AddedAt)
            .ThenByDescending(s => s.Id, StringComparer.Ordinal)
            .FirstOrDefault()?.Currency;
    }

    /// <summary>List order: name (ordinal), then start date, then id.</summary>
    public static IReadOnlyList<SubscriptionState> ListOrder(IEnumerable<SubscriptionState> subscriptions)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);
        return subscriptions
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ThenBy(s => s.StartDate)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .ToList();
    }
}
