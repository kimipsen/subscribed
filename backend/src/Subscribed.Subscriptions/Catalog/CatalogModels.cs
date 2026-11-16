namespace Subscribed.Subscriptions;

/// <summary>A planned price change published by the provider. Shown read-only; never turned into a price entry (R6.2).</summary>
public sealed record CatalogUpcomingChange(decimal Amount, DateOnly EffectiveFrom);

/// <summary>One purchasable plan of a catalog service, in one market.</summary>
public sealed record CatalogPlan(
    string Country,
    string Name,
    string Currency,
    decimal Amount,
    string CadenceUnit,
    int CadenceCount,
    CatalogUpcomingChange? UpcomingChange);

/// <summary>
/// A known service. <see cref="Logo"/> is a relative path under <c>/logos/</c> or null (initial-letter fallback).
/// <see cref="UnsubscribeUrl"/> is an official https cancel or manage-subscription page, or null when none is confirmed.
/// </summary>
public sealed record CatalogService(
    string Id,
    string Name,
    string Category,
    string? Logo,
    DateOnly LastVerified,
    string SourceUrl,
    IReadOnlyList<CatalogPlan> Plans,
    string? UnsubscribeUrl = null);

/// <summary>Thrown when the catalog data is invalid. The message names the service id and field.</summary>
public sealed class CatalogValidationException(string message) : Exception(message);
