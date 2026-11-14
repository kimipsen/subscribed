using System.Text.Json;
using System.Text.Json.Serialization;

namespace Subscribed.Subscriptions.Domain;

// v1 events are immutable. A version may only gain optional fields; a breaking change gets a new alias
// (...v2) plus an upcaster. Payloads are primitives only, and hold no claims, emails or tokens: the actor
// and the correlation id travel in Marten headers.

public sealed record SubscriptionAdded(
    string HouseholdId,
    string Name,
    string? Category,
    string? Notes,
    string Currency,
    DateOnly StartDate,
    DateOnly? EndDate,
    string InitialEntryId,
    decimal Amount,
    string CadenceUnit,
    int CadenceCount,
    DateTimeOffset RecordedAt);

public sealed record SubscriptionDetailsChanged(string Name, string? Category, string? Notes, DateTimeOffset RecordedAt);

public sealed record SubscriptionCurrencyCorrected(string Currency, DateTimeOffset RecordedAt);

public sealed record SubscriptionPeriodChanged(DateOnly StartDate, DateOnly? EndDate, DateTimeOffset RecordedAt);

public sealed record SubscriptionPriceEntryAdded(string EntryId, DateOnly EffectiveFrom, decimal Amount, string CadenceUnit, int CadenceCount, DateTimeOffset RecordedAt);

public sealed record SubscriptionPriceEntryCorrected(string EntryId, DateOnly EffectiveFrom, decimal Amount, string CadenceUnit, int CadenceCount, DateTimeOffset RecordedAt);

public sealed record SubscriptionPriceEntryRemoved(string EntryId, DateTimeOffset RecordedAt);

public sealed record SubscriptionRemoved(DateTimeOffset RecordedAt);

public sealed class UnknownSubscriptionEventAliasException(string alias)
    : Exception($"Unknown subscription event alias '{alias}'. Stored events are never skipped.")
{
    public string Alias { get; } = alias;
}

/// <summary>Explicit, versioned event aliases. Marten stores these in mt_events.type.</summary>
public static class SubscriptionEventTypes
{
    public const string Added = "subscription.added.v1";
    public const string DetailsChanged = "subscription.details_changed.v1";
    public const string CurrencyCorrected = "subscription.currency_corrected.v1";
    public const string PeriodChanged = "subscription.period_changed.v1";
    public const string PriceEntryAdded = "subscription.price_entry_added.v1";
    public const string PriceEntryCorrected = "subscription.price_entry_corrected.v1";
    public const string PriceEntryRemoved = "subscription.price_entry_removed.v1";
    public const string Removed = "subscription.removed.v1";

    public static readonly IReadOnlyDictionary<string, Type> ByAlias = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        [Added] = typeof(SubscriptionAdded),
        [DetailsChanged] = typeof(SubscriptionDetailsChanged),
        [CurrencyCorrected] = typeof(SubscriptionCurrencyCorrected),
        [PeriodChanged] = typeof(SubscriptionPeriodChanged),
        [PriceEntryAdded] = typeof(SubscriptionPriceEntryAdded),
        [PriceEntryCorrected] = typeof(SubscriptionPriceEntryCorrected),
        [PriceEntryRemoved] = typeof(SubscriptionPriceEntryRemoved),
        [Removed] = typeof(SubscriptionRemoved),
    };

    /// <summary>
    /// The JSON shape Marten is configured with (System.Text.Json, camelCase). For events only: request bodies use their
    /// own options. Amounts are JSON numbers, stored exactly as jsonb numeric.
    /// </summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public static Type Resolve(string alias) =>
        ByAlias.TryGetValue(alias, out var type) ? type : throw new UnknownSubscriptionEventAliasException(alias);

    public static string AliasOf(Type type) =>
        ByAlias.FirstOrDefault(kv => kv.Value == type).Key ?? throw new ArgumentException($"Type {type.Name} has no alias.", nameof(type));

    public static object Deserialize(string alias, string json) =>
        JsonSerializer.Deserialize(json, Resolve(alias), Json) ?? throw new JsonException($"Event payload for '{alias}' is null.");

    public static string Serialize(object @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        AliasOf(@event.GetType());
        return JsonSerializer.Serialize(@event, @event.GetType(), Json);
    }
}
