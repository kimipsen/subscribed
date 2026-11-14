namespace Subscribed.Subscriptions.Domain;

public enum SubscriptionStatus
{
    NotStarted,
    Active,
    Ended,
}

public static class SubscriptionStatusExtensions
{
    /// <summary>kebab-case, like the session states.</summary>
    public static string ToWire(this SubscriptionStatus status) => status switch
    {
        SubscriptionStatus.NotStarted => "not-started",
        SubscriptionStatus.Active => "active",
        SubscriptionStatus.Ended => "ended",
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

public sealed record CurrencyTotal(string Currency, int MinorUnit, decimal Monthly, decimal Yearly, int ActiveCount);

/// <summary>Read-side calculations. Pure functions: nothing time-dependent is stored (BR-021).</summary>
public static class SubscriptionCalculator
{
    public static DateOnly Today(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        return DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
    }

    /// <summary>The end date is inclusive (BR-006).</summary>
    public static SubscriptionStatus StatusOn(SubscriptionState state, DateOnly day)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (day < state.StartDate)
        {
            return SubscriptionStatus.NotStarted;
        }

        return state.EndDate is { } end && day > end ? SubscriptionStatus.Ended : SubscriptionStatus.Active;
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
    /// Per currency, over non-removed subscriptions that are active today: the unrounded normalized monthly and yearly costs of
    /// their current prices are summed and rounded once. Sorted by code. Currencies are never combined.
    /// </summary>
    public static IReadOnlyList<CurrencyTotal> Totals(IEnumerable<SubscriptionState> subscriptions, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);
        return subscriptions
            .Where(s => !s.Removed && StatusOn(s, today) == SubscriptionStatus.Active)
            .GroupBy(s => s.Currency, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var minor = MinorUnitOf(g.First());
                var monthly = Fraction.Zero;
                var yearly = Fraction.Zero;
                foreach (var s in g)
                {
                    var price = CurrentPrice(s, today);
                    monthly += price.Cadence.Monthly(price.Amount);
                    yearly += price.Cadence.Yearly(price.Amount);
                }

                return new CurrencyTotal(g.Key, minor, Money.RoundHalfAwayFromZero(monthly, minor), Money.RoundHalfAwayFromZero(yearly, minor), g.Count());
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
