using System.Text.Json;
using System.Text.Json.Serialization;

namespace Subscribed.Household.Domain;

// v1 events are immutable. A version may only gain optional fields; a breaking change gets a new
// alias (...v2) plus an upcaster. No emails, tokens or other claims are ever stored.

public sealed record HouseholdCreated(string Name, DateTimeOffset CreatedAt);

public sealed record HouseholdRenamed(string Name, DateTimeOffset RenamedAt);

public sealed record HouseholdMemberGranted(string MemberId, string Issuer, string Subject, string DisplayLabel, DateTimeOffset GrantedAt);

public sealed record HouseholdMemberRevoked(string MemberId, DateTimeOffset RevokedAt);

public sealed class UnknownEventAliasException(string alias)
    : Exception($"Unknown household event alias '{alias}'. Stored events are never skipped.")
{
    public string Alias { get; } = alias;
}

/// <summary>Explicit, versioned event aliases. Marten stores these in mt_events.type.</summary>
public static class HouseholdEventTypes
{
    public const string Created = "household.created.v1";
    public const string Renamed = "household.renamed.v1";
    public const string MemberGranted = "household.member_granted.v1";
    public const string MemberRevoked = "household.member_revoked.v1";

    public static readonly IReadOnlyDictionary<string, Type> ByAlias = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        [Created] = typeof(HouseholdCreated),
        [Renamed] = typeof(HouseholdRenamed),
        [MemberGranted] = typeof(HouseholdMemberGranted),
        [MemberRevoked] = typeof(HouseholdMemberRevoked),
    };

    /// <summary>The same JSON shape Marten is configured with (System.Text.Json, camelCase).</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public static Type Resolve(string alias) =>
        ByAlias.TryGetValue(alias, out var type) ? type : throw new UnknownEventAliasException(alias);

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
