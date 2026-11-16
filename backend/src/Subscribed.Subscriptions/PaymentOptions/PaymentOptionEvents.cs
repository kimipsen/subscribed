using System.Text.Json;
using System.Text.Json.Serialization;

namespace Subscribed.Subscriptions.PaymentOptions;

// v1 events are immutable (see SubscriptionEvents). Payment option events carry no card data: only a name and an optional
// label that passed the card-number rule (D7). The actor and the correlation id travel in Marten headers.

public sealed record PaymentOptionCreated(string HouseholdId, string Name, string? Label, DateTimeOffset RecordedAt);

public sealed record PaymentOptionChanged(string Name, string? Label, DateTimeOffset RecordedAt);

public sealed record PaymentOptionRemoved(DateTimeOffset RecordedAt);

public sealed class UnknownPaymentOptionEventAliasException(string alias)
    : Exception($"Unknown payment option event alias '{alias}'. Stored events are never skipped.")
{
    public string Alias { get; } = alias;
}

/// <summary>
/// Explicit, versioned aliases of the payment option streams. They live only here: <c>SubscriptionEventTypes.ByAlias</c> must
/// keep holding <c>subscription.*</c> aliases only.
/// </summary>
public static class PaymentOptionEventTypes
{
    public const string Created = "payment_option.created.v1";
    public const string Changed = "payment_option.changed.v1";
    public const string Removed = "payment_option.removed.v1";

    public static readonly IReadOnlyDictionary<string, Type> ByAlias = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        [Created] = typeof(PaymentOptionCreated),
        [Changed] = typeof(PaymentOptionChanged),
        [Removed] = typeof(PaymentOptionRemoved),
    };

    /// <summary>The same JSON shape as the subscription events (System.Text.Json, camelCase).</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public static Type Resolve(string alias) =>
        ByAlias.TryGetValue(alias, out var type) ? type : throw new UnknownPaymentOptionEventAliasException(alias);

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
