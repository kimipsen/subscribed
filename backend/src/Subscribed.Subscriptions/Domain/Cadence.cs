namespace Subscribed.Subscriptions.Domain;

/// <summary>A billing cadence: every <see cref="Count"/> weeks, months or years (BR-010).</summary>
public readonly record struct Cadence(string Unit, int Count)
{
    public const string Week = "week";
    public const string Month = "month";
    public const string Year = "year";
    public const int MaxCount = 99;

    public static bool IsValidUnit(string? unit) => unit is Week or Month or Year;

    public static bool IsValidCount(int? count) => count is >= 1 and <= MaxCount;

    public static bool TryCreate(string? unit, int? count, out Cadence cadence)
    {
        cadence = default;
        if (!IsValidUnit(unit) || !IsValidCount(count))
        {
            return false;
        }

        cadence = new Cadence(unit!, count!.Value);
        return true;
    }

    private int PeriodsPerYearNumerator => Unit switch
    {
        Week => 52,
        Month => 12,
        Year => 1,
        _ => throw new InvalidOperationException($"Unknown cadence unit '{Unit}'."),
    };

    /// <summary>Yearly cost, unrounded: amount × 52 / n for weeks, × 12 / n for months, × 1 / n for years (BR-011).</summary>
    public Fraction Yearly(decimal amount) => Fraction.FromDecimal(amount) * PeriodsPerYearNumerator / Count;

    /// <summary>Monthly cost, unrounded: the yearly cost divided by 12.</summary>
    public Fraction Monthly(decimal amount) => Yearly(amount) / 12;
}
