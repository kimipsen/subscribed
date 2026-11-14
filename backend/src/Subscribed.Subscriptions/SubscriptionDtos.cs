using Subscribed.Subscriptions.Domain;

namespace Subscribed.Subscriptions;

// Requests are flat, with strings for money and dates. Every property is nullable: omitted means null (D-10), so a missing or
// wrongly typed field is reported under its own key. Responses send amounts as strings with exactly the currency's minor
// unit's fraction digits; the server computes every derived value.

public sealed record AddSubscriptionRequest(
    string? Name,
    string? Category,
    string? Notes,
    string? Currency,
    string? StartDate,
    string? EndDate,
    string? Amount,
    string? CadenceUnit,
    int? CadenceCount)
{
    public AddInput ToInput() => new(Name, Category, Notes, Currency, StartDate, EndDate, Amount, CadenceUnit, CadenceCount);
}

/// <summary>Fully replaces the editable details: sending a null <c>endDate</c> reinstates the subscription.</summary>
public sealed record UpdateSubscriptionRequest(string? Name, string? Category, string? Notes, string? Currency, string? StartDate, string? EndDate)
{
    public UpdateInput ToInput() => new(Name, Category, Notes, Currency, StartDate, EndDate);
}

public sealed record PriceEntryRequest(string? EffectiveFrom, string? Amount, string? CadenceUnit, int? CadenceCount)
{
    public PriceInput ToInput() => new(EffectiveFrom, Amount, CadenceUnit, CadenceCount);
}

public sealed record PriceDto(string EntryId, DateOnly EffectiveFrom, string Amount, string CadenceUnit, int CadenceCount)
{
    public static PriceDto From(PriceEntryState entry, int minorUnit) =>
        new(entry.EntryId, entry.EffectiveFrom, Money.FormatAmount(entry.Amount, minorUnit), entry.CadenceUnit, entry.CadenceCount);
}

public sealed record PriceChangeDto(string Amount, string? Percent);

public sealed record HistoryEntryDto(
    string EntryId,
    bool Initial,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    string Amount,
    string CadenceUnit,
    int CadenceCount,
    string NormalizedMonthly,
    string NormalizedYearly,
    PriceChangeDto? Change,
    bool Current,
    bool Upcoming);

public sealed record SubscriptionSummaryDto(
    string Id,
    string Name,
    string? Category,
    string Status,
    DateOnly StartDate,
    DateOnly? EndDate,
    string Currency,
    int MinorUnit,
    PriceDto CurrentPrice,
    string NormalizedMonthly,
    string NormalizedYearly,
    bool UpcomingPriceChange)
{
    public static SubscriptionSummaryDto From(SubscriptionState state, DateOnly today)
    {
        var minor = SubscriptionCalculator.MinorUnitOf(state);
        var current = SubscriptionCalculator.CurrentPrice(state, today);
        return new SubscriptionSummaryDto(
            SubscriptionIds.Bare(state.Id),
            state.Name,
            state.Category,
            SubscriptionCalculator.StatusOn(state, today).ToWire(),
            state.StartDate,
            state.EndDate,
            state.Currency,
            minor,
            PriceDto.From(current, minor),
            Money.FormatAmount(SubscriptionCalculator.NormalizedMonthly(current, minor), minor),
            Money.FormatAmount(SubscriptionCalculator.NormalizedYearly(current, minor), minor),
            SubscriptionCalculator.UpcomingPriceChange(state, today));
    }
}

public sealed record CurrencyTotalDto(string Currency, int MinorUnit, string Monthly, string Yearly, int ActiveCount);

public sealed record SubscriptionOverviewDto(DateOnly AsOf, IReadOnlyList<SubscriptionSummaryDto> Subscriptions, IReadOnlyList<CurrencyTotalDto> Totals)
{
    public static SubscriptionOverviewDto From(IReadOnlyList<SubscriptionState> subscriptions, DateOnly today) =>
        new(
            today,
            [.. SubscriptionCalculator.ListOrder(subscriptions).Select(s => SubscriptionSummaryDto.From(s, today))],
            [.. SubscriptionCalculator.Totals(subscriptions, today).Select(t =>
                new CurrencyTotalDto(t.Currency, t.MinorUnit, Money.FormatAmount(t.Monthly, t.MinorUnit), Money.FormatAmount(t.Yearly, t.MinorUnit), t.ActiveCount))]);
}

public sealed record SubscriptionDetailDto(
    string Id,
    long Version,
    DateOnly AsOf,
    string Name,
    string? Category,
    string? Notes,
    string Status,
    DateOnly StartDate,
    DateOnly? EndDate,
    string Currency,
    int MinorUnit,
    PriceDto CurrentPrice,
    string NormalizedMonthly,
    string NormalizedYearly,
    IReadOnlyList<HistoryEntryDto> PriceHistory)
{
    public static SubscriptionDetailDto From(SubscriptionState state, DateOnly today)
    {
        var minor = SubscriptionCalculator.MinorUnitOf(state);
        var current = SubscriptionCalculator.CurrentPrice(state, today);
        return new SubscriptionDetailDto(
            SubscriptionIds.Bare(state.Id),
            state.Version,
            today,
            state.Name,
            state.Category,
            state.Notes,
            SubscriptionCalculator.StatusOn(state, today).ToWire(),
            state.StartDate,
            state.EndDate,
            state.Currency,
            minor,
            PriceDto.From(current, minor),
            Money.FormatAmount(SubscriptionCalculator.NormalizedMonthly(current, minor), minor),
            Money.FormatAmount(SubscriptionCalculator.NormalizedYearly(current, minor), minor),
            [.. SubscriptionCalculator.History(state, today).Select(r => new HistoryEntryDto(
                r.Entry.EntryId,
                r.Initial,
                r.EffectiveFrom,
                r.EffectiveTo,
                Money.FormatAmount(r.Entry.Amount, minor),
                r.Entry.CadenceUnit,
                r.Entry.CadenceCount,
                Money.FormatAmount(r.NormalizedMonthly, minor),
                Money.FormatAmount(r.NormalizedYearly, minor),
                r.Change is { } c ? new PriceChangeDto(Money.FormatAmount(c.Amount, minor), c.Percent is { } p ? Money.FormatAmount(p, 1) : null) : null,
                r.Current,
                r.Upcoming))]);
    }
}

public sealed record CurrencyOptionDto(string Code, int MinorUnit);

public sealed record SubscriptionOptionsDto(IReadOnlyList<CurrencyOptionDto> Currencies, string? DefaultCurrency)
{
    public static SubscriptionOptionsDto From(IReadOnlyList<SubscriptionState> subscriptions) =>
        new(
            [.. Iso4217.Codes.Select(code => Iso4217.TryGetMinorUnit(code, out var minor) ? new CurrencyOptionDto(code, minor) : throw new InvalidOperationException(code))],
            SubscriptionCalculator.DefaultCurrency(subscriptions));
}
