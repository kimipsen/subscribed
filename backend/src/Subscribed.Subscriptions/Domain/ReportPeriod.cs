using System.Globalization;

namespace Subscribed.Subscriptions.Domain;

public enum ReportPeriodKind
{
    CalendarYear,
    Last12Months,
    Next12Months,
}

/// <summary>
/// The window a period total is computed over, both ends inclusive. Every offered period is exactly twelve months (D2), so
/// "average per month = total / 12" holds for all of them.
/// </summary>
public sealed record ReportPeriod(ReportPeriodKind Kind, int? Year, DateOnly From, DateOnly To)
{
    public const int Months = 12;

    public const int MinYear = 1900;

    public const int MaxYear = 9999;

    private const string YearPrefix = "year-";
    private const int YearLength = 9;
    private const string Last12Wire = "last-12-months";
    private const string Next12Wire = "next-12-months";

    /// <summary>The wire key: <c>year-2026</c>, <c>last-12-months</c> or <c>next-12-months</c>.</summary>
    public string ToWire() => Kind switch
    {
        ReportPeriodKind.CalendarYear => YearPrefix + Year!.Value.ToString("D4", CultureInfo.InvariantCulture),
        ReportPeriodKind.Last12Months => Last12Wire,
        ReportPeriodKind.Next12Months => Next12Wire,
        _ => throw new InvalidOperationException($"Unknown report period kind {Kind}."),
    };

    /// <summary>
    /// Parses a wire key. A year is exactly four ASCII digits in 1900..9999, which also guards <see cref="DateOnly"/> overflow. The
    /// rolling windows follow D2: last 12 months is <c>today - 12 months + 1 day</c> through today, next 12 months is today through
    /// <c>today + 12 months - 1 day</c>.
    /// </summary>
    public static bool TryParse(string? wire, DateOnly today, out ReportPeriod period)
    {
        period = CurrentYear(today);
        switch (wire)
        {
            case Last12Wire:
                period = new ReportPeriod(ReportPeriodKind.Last12Months, null, SafeAddMonths(today, -Months).AddDays(1), today);
                return true;
            case Next12Wire:
                period = new ReportPeriod(ReportPeriodKind.Next12Months, null, today, SafeAddMonths(today, Months).AddDays(-1));
                return true;
        }

        if (wire is not { Length: YearLength } || !wire.StartsWith(YearPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var digits = wire.AsSpan(YearPrefix.Length);
        foreach (var c in digits)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        var year = int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
        if (year is < MinYear or > MaxYear)
        {
            return false;
        }

        period = ForYear(year);
        return true;
    }

    public static ReportPeriod CurrentYear(DateOnly today) => ForYear(today.Year);

    public static ReportPeriod ForYear(int year) =>
        new(ReportPeriodKind.CalendarYear, year, new DateOnly(year, 1, 1), new DateOnly(year, 12, 31));

    /// <summary>
    /// The calendar years the selector offers (D3): the earliest start year of the non-removed subscriptions through next year, or the
    /// current year through next year when there is none. Never fewer than the current year and next year.
    /// </summary>
    public static IReadOnlyList<int> Years(IEnumerable<SubscriptionState> subscriptions, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(subscriptions);
        var first = today.Year;
        foreach (var s in subscriptions)
        {
            if (!s.Removed && s.StartDate.Year < first)
            {
                first = s.StartDate.Year;
            }
        }

        first = Math.Max(first, MinYear);
        var last = Math.Min(today.Year + 1, MaxYear);
        return [.. Enumerable.Range(first, last - first + 1)];
    }

    // DateOnly.AddMonths throws at the ends of the range; a window clamped to the range edge is still a sensible answer there.
    private static DateOnly SafeAddMonths(DateOnly day, int months)
    {
        try
        {
            return day.AddMonths(months);
        }
        catch (ArgumentOutOfRangeException)
        {
            return months < 0 ? DateOnly.MinValue : DateOnly.MaxValue;
        }
    }
}
