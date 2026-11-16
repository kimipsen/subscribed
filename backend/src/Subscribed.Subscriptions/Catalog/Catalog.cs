namespace Subscribed.Subscriptions;

/// <summary>
/// Immutable, in-memory lookup of the known services. Service order and plan order are the file order and are significant:
/// both are preserved in the API response.
/// </summary>
public sealed class Catalog
{
    public const string DefaultMarket = "DK";

    private readonly Dictionary<string, CatalogService> _byId;

    public Catalog(IEnumerable<CatalogService> services, string defaultCountry = DefaultMarket)
    {
        ArgumentNullException.ThrowIfNull(services);
        Services = [.. services];
        DefaultCountry = defaultCountry;
        _byId = new Dictionary<string, CatalogService>(Services.Count, StringComparer.Ordinal);
        foreach (var service in Services)
        {
            if (!_byId.TryAdd(service.Id, service))
            {
                throw new ArgumentException($"Duplicate catalog service id '{service.Id}'.", nameof(services));
            }
        }
    }

    public static Catalog Empty { get; } = new([]);

    public IReadOnlyList<CatalogService> Services { get; }

    public string DefaultCountry { get; }

    /// <summary>The service with this id, or null when the id is null or unknown.</summary>
    public CatalogService? TryGet(string? id) => id is not null && _byId.TryGetValue(id, out var service) ? service : null;

    public DateOnly? OldestVerified => Services.Count == 0 ? null : Services.Min(static s => s.LastVerified);
}
