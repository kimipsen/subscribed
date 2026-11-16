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
    int? CadenceCount,
    string? CatalogId,
    string? PaymentOptionId = null)
{
    public AddInput ToInput() => new(Name, Category, Notes, Currency, StartDate, EndDate, Amount, CadenceUnit, CadenceCount, CatalogId, PaymentOptionId);
}

/// <summary>Fully replaces the editable details: a null <c>endDate</c> reinstates the subscription and a null or omitted <c>catalogId</c> unlinks it (P7).</summary>
public sealed record UpdateSubscriptionRequest(string? Name, string? Category, string? Notes, string? Currency, string? StartDate, string? EndDate, string? CatalogId)
{
    public UpdateInput ToInput() => new(Name, Category, Notes, Currency, StartDate, EndDate, CatalogId);
}

/// <summary>A null or omitted <c>paymentOptionId</c> means "no payment option": the same full-replace semantics as <c>catalogId</c> (D9).</summary>
public sealed record PriceEntryRequest(string? EffectiveFrom, string? Amount, string? CadenceUnit, int? CadenceCount, string? PaymentOptionId = null)
{
    public PriceInput ToInput() => new(EffectiveFrom, Amount, CadenceUnit, CadenceCount, PaymentOptionId);
}

public sealed record PriceDto(string EntryId, DateOnly EffectiveFrom, string Amount, string CadenceUnit, int CadenceCount, string? PaymentOptionId = null)
{
    public static PriceDto From(PriceEntryState entry, int minorUnit) =>
        new(entry.EntryId, entry.EffectiveFrom, Money.FormatAmount(entry.Amount, minorUnit), entry.CadenceUnit, entry.CadenceCount, entry.PaymentOptionId);
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
    bool Upcoming,
    string? PaymentOptionId = null);

public sealed record SubscriptionSummaryDto(
    string Id,
    long Version,
    string Name,
    string? Category,
    string? CatalogId,
    string? LogoUrl,
    string Status,
    bool Paused,
    DateOnly StartDate,
    DateOnly? EndDate,
    string Currency,
    int MinorUnit,
    PriceDto CurrentPrice,
    string NormalizedMonthly,
    string NormalizedYearly,
    bool UpcomingPriceChange)
{
    public static SubscriptionSummaryDto From(SubscriptionState state, DateOnly today, Catalog catalog)
    {
        var minor = SubscriptionCalculator.MinorUnitOf(state);
        var current = SubscriptionCalculator.CurrentPrice(state, today);
        return new SubscriptionSummaryDto(
            SubscriptionIds.Bare(state.Id),
            state.Version,
            state.Name,
            state.Category,
            state.CatalogId,
            catalog.TryGet(state.CatalogId)?.Logo,
            SubscriptionCalculator.StatusOn(state, today).ToWire(),
            state.Paused,
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
    public static SubscriptionOverviewDto From(IReadOnlyList<SubscriptionState> subscriptions, DateOnly today, Catalog catalog) =>
        new(
            today,
            [.. SubscriptionCalculator.ListOrder(subscriptions).Select(s => SubscriptionSummaryDto.From(s, today, catalog))],
            [.. SubscriptionCalculator.Totals(subscriptions, today).Select(t =>
                new CurrencyTotalDto(t.Currency, t.MinorUnit, Money.FormatAmount(t.Monthly, t.MinorUnit), Money.FormatAmount(t.Yearly, t.MinorUnit), t.ActiveCount))]);
}

public sealed record SubscriptionDetailDto(
    string Id,
    long Version,
    DateOnly AsOf,
    string Name,
    string? Category,
    string? CatalogId,
    string? LogoUrl,
    string? Notes,
    string Status,
    bool Paused,
    DateOnly StartDate,
    DateOnly? EndDate,
    string Currency,
    int MinorUnit,
    PriceDto CurrentPrice,
    string NormalizedMonthly,
    string NormalizedYearly,
    IReadOnlyList<HistoryEntryDto> PriceHistory)
{
    public static SubscriptionDetailDto From(SubscriptionState state, DateOnly today, Catalog catalog)
    {
        var minor = SubscriptionCalculator.MinorUnitOf(state);
        var current = SubscriptionCalculator.CurrentPrice(state, today);
        return new SubscriptionDetailDto(
            SubscriptionIds.Bare(state.Id),
            state.Version,
            today,
            state.Name,
            state.Category,
            state.CatalogId,
            catalog.TryGet(state.CatalogId)?.Logo,
            state.Notes,
            SubscriptionCalculator.StatusOn(state, today).ToWire(),
            state.Paused,
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
                r.Upcoming,
                r.Entry.PaymentOptionId))]);
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

public sealed record CatalogUpcomingChangeDto(string Amount, DateOnly EffectiveFrom);

public sealed record CatalogPlanDto(
    string Country,
    string Name,
    string Currency,
    int MinorUnit,
    string Amount,
    string CadenceUnit,
    int CadenceCount,
    CatalogUpcomingChangeDto? UpcomingChange);

public sealed record CatalogServiceDto(
    string Id,
    string Name,
    string Category,
    string? LogoUrl,
    DateOnly LastVerified,
    IReadOnlyList<CatalogPlanDto> Plans);

/// <summary>The catalog as the API shows it. File order of services and plans is preserved; <c>sourceUrl</c> is not exposed.</summary>
public sealed record CatalogDto(IReadOnlyList<CatalogServiceDto> Services, string DefaultCountry)
{
    public static CatalogDto From(Catalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return new CatalogDto(
            [.. catalog.Services.Select(static service => new CatalogServiceDto(
                service.Id,
                service.Name,
                service.Category,
                service.Logo,
                service.LastVerified,
                [.. service.Plans.Select(static plan => PlanFrom(plan))]))],
            catalog.DefaultCountry);
    }

    private static CatalogPlanDto PlanFrom(CatalogPlan plan)
    {
        Iso4217.TryGetMinorUnit(plan.Currency, out var minor);
        return new CatalogPlanDto(
            plan.Country,
            plan.Name,
            plan.Currency,
            minor,
            Money.FormatAmount(plan.Amount, minor),
            plan.CadenceUnit,
            plan.CadenceCount,
            plan.UpcomingChange is { } change ? new CatalogUpcomingChangeDto(Money.FormatAmount(change.Amount, minor), change.EffectiveFrom) : null);
    }
}
