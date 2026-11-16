using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Subscribed.Subscriptions.Domain;

namespace Subscribed.Subscriptions;

/// <summary>Parses and validates <c>catalog.json</c>. Any problem throws <see cref="CatalogValidationException"/>.</summary>
public static partial class CatalogLoader
{
    public const string ResourceName = "catalog.json";
    public const int SupportedSchemaVersion = 1;
    public const int MaxIdLength = 64;

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^/logos/[a-z0-9-]+\.(svg|png|webp)$")]
    private static partial Regex LogoPattern();

    /// <summary>The same shape rule the loader applies to service ids, reused for the <c>catalogId</c> field.</summary>
    public static bool IsValidId(string? id) => id is { Length: > 0 and <= MaxIdLength } && IdPattern().IsMatch(id);

    public static Catalog LoadEmbedded(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        var assembly = typeof(CatalogLoader).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new CatalogValidationException($"Catalog resource '{ResourceName}' is missing from {assembly.GetName().Name}.");
        return Load(stream, DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime));
    }

    public static Catalog Load(Stream json, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(json);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow });
        }
        catch (JsonException ex)
        {
            throw new CatalogValidationException($"Catalog: malformed JSON ({ex.Message})");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new CatalogValidationException("Catalog: the root must be a JSON object.");
            }

            if (!root.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var schema) || schema != SupportedSchemaVersion)
            {
                throw new CatalogValidationException($"Catalog: schemaVersion must be {SupportedSchemaVersion.ToString(CultureInfo.InvariantCulture)}.");
            }

            if (!root.TryGetProperty("services", out var services) || services.ValueKind != JsonValueKind.Array)
            {
                throw new CatalogValidationException("Catalog: services must be an array.");
            }

            var result = new List<CatalogService>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var index = 0;
            foreach (var element in services.EnumerateArray())
            {
                var service = ReadService(element, index, today);
                if (!seen.Add(service.Id))
                {
                    throw new CatalogValidationException($"Catalog service '{service.Id}': field 'id' is a duplicate.");
                }

                result.Add(service);
                index++;
            }

            return new Catalog(result);
        }
    }

    private static CatalogService ReadService(JsonElement element, int index, DateOnly today)
    {
        var label = $"services[{index.ToString(CultureInfo.InvariantCulture)}]";
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new CatalogValidationException($"Catalog service '{label}': must be an object.");
        }

        var id = String(element, "id", label);
        if (!IsValidId(id))
        {
            throw new CatalogValidationException($"Catalog service '{id}': field 'id' must be lowercase kebab-case, at most {MaxIdLength.ToString(CultureInfo.InvariantCulture)} characters.");
        }

        var name = String(element, "name", id);
        if (!SubscriptionText.TryNormalizeName(name, out var normalizedName) || normalizedName != name)
        {
            throw new CatalogValidationException($"Catalog service '{id}': field 'name' must be trimmed text of 1 to {SubscriptionText.MaxName.ToString(CultureInfo.InvariantCulture)} characters without control characters.");
        }

        var category = String(element, "category", id);
        if (!SubscriptionText.TryNormalizeCategory(category, out var normalizedCategory) || normalizedCategory is null || normalizedCategory != category)
        {
            throw new CatalogValidationException($"Catalog service '{id}': field 'category' must be trimmed text of 1 to {SubscriptionText.MaxCategory.ToString(CultureInfo.InvariantCulture)} characters without control characters.");
        }

        string? logo = null;
        if (element.TryGetProperty("logo", out var logoElement) && logoElement.ValueKind != JsonValueKind.Null)
        {
            if (logoElement.ValueKind != JsonValueKind.String || !LogoPattern().IsMatch(logoElement.GetString()!))
            {
                throw new CatalogValidationException($"Catalog service '{id}': field 'logo' must be null or a path like /logos/name.svg (svg, png or webp).");
            }

            logo = logoElement.GetString();
        }

        var lastVerified = Date(String(element, "lastVerified", id), "lastVerified", id);
        if (lastVerified > today.AddDays(1))
        {
            throw new CatalogValidationException($"Catalog service '{id}': field 'lastVerified' ({lastVerified:yyyy-MM-dd}) is in the future.");
        }

        var sourceUrl = String(element, "sourceUrl", id);
        if (!Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new CatalogValidationException($"Catalog service '{id}': field 'sourceUrl' must be an absolute https URL.");
        }

        var unsubscribeUrl = ReadUnsubscribeUrl(element, id);

        if (!element.TryGetProperty("plans", out var plans) || plans.ValueKind != JsonValueKind.Array)
        {
            throw new CatalogValidationException($"Catalog service '{id}': field 'plans' must be an array.");
        }

        var list = new List<CatalogPlan>();
        var planIndex = 0;
        foreach (var plan in plans.EnumerateArray())
        {
            list.Add(ReadPlan(plan, id, planIndex++));
        }

        return new CatalogService(id, name, category, logo, lastVerified, sourceUrl, list, unsubscribeUrl);
    }

    /// <summary>
    /// Optional. Absent or null means none; a present value must be a string that passes the shared user-input rule and equals its
    /// trimmed form. The message names the service and field, never the value.
    /// </summary>
    private static string? ReadUnsubscribeUrl(JsonElement element, string id)
    {
        if (!element.TryGetProperty("unsubscribeUrl", out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        var raw = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (raw is null || raw.Length == 0 || !SubscriptionText.TryNormalizeUnsubscribeUrl(raw, out var url) || url is null || url != raw)
        {
            throw new CatalogValidationException($"Catalog service '{id}': field 'unsubscribeUrl' must be an absolute https URL.");
        }

        return url;
    }

    private static CatalogPlan ReadPlan(JsonElement element, string serviceId, int index)
    {
        var path = $"plans[{index.ToString(CultureInfo.InvariantCulture)}]";
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new CatalogValidationException($"Catalog service '{serviceId}': field '{path}' must be an object.");
        }

        var country = Plan(element, "country", serviceId, path);
        if (country.Length != 2 || !country.All(static c => c is >= 'A' and <= 'Z'))
        {
            throw new CatalogValidationException($"Catalog service '{serviceId}': field '{path}.country' must be two uppercase letters.");
        }

        var name = Plan(element, "name", serviceId, path);
        if (!SubscriptionText.TryNormalizeName(name, out var normalizedName) || normalizedName != name)
        {
            throw new CatalogValidationException($"Catalog service '{serviceId}': field '{path}.name' must be trimmed text of 1 to {SubscriptionText.MaxName.ToString(CultureInfo.InvariantCulture)} characters.");
        }

        var currency = Plan(element, "currency", serviceId, path);
        if (!Iso4217.TryGetMinorUnit(currency, out var minorUnit) || !Iso4217.IsValid(currency))
        {
            throw new CatalogValidationException($"Catalog service '{serviceId}': field '{path}.currency' is not a supported ISO 4217 code.");
        }

        var amount = Amount(Plan(element, "amount", serviceId, path), $"{path}.amount", serviceId, minorUnit);

        var unit = Plan(element, "cadenceUnit", serviceId, path);
        if (!element.TryGetProperty("cadenceCount", out var countElement) || countElement.ValueKind != JsonValueKind.Number
            || !countElement.TryGetInt32(out var count) || !Cadence.TryCreate(unit, count, out _))
        {
            throw new CatalogValidationException($"Catalog service '{serviceId}': field '{path}.cadenceUnit/cadenceCount' must be week, month or year and a count from 1 to {Cadence.MaxCount.ToString(CultureInfo.InvariantCulture)}.");
        }

        CatalogUpcomingChange? upcoming = null;
        if (element.TryGetProperty("upcomingChange", out var change) && change.ValueKind != JsonValueKind.Null)
        {
            if (change.ValueKind != JsonValueKind.Object)
            {
                throw new CatalogValidationException($"Catalog service '{serviceId}': field '{path}.upcomingChange' must be an object or null.");
            }

            var changeAmount = Amount(Plan(change, "amount", serviceId, path + ".upcomingChange"), $"{path}.upcomingChange.amount", serviceId, minorUnit);
            var from = Date(Plan(change, "effectiveFrom", serviceId, path + ".upcomingChange"), $"{path}.upcomingChange.effectiveFrom", serviceId);
            upcoming = new CatalogUpcomingChange(changeAmount, from);
        }

        return new CatalogPlan(country, name, currency, amount, unit, count, upcoming);
    }

    private static decimal Amount(string text, string field, string serviceId, int minorUnit)
    {
        if (!Money.TryParseAmount(text, out var amount) || amount <= 0m || !Money.HasAtMostFractionDigits(amount, minorUnit))
        {
            throw new CatalogValidationException($"Catalog service '{serviceId}': field '{field}' must be a positive amount with at most {minorUnit.ToString(CultureInfo.InvariantCulture)} decimals for the currency.");
        }

        return amount;
    }

    private static DateOnly Date(string text, string field, string serviceId)
    {
        if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            throw new CatalogValidationException($"Catalog service '{serviceId}': field '{field}' must be a valid yyyy-MM-dd date.");
        }

        return date;
    }

    private static string String(JsonElement element, string field, string serviceId) =>
        element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new CatalogValidationException($"Catalog service '{serviceId}': field '{field}' must be a string.");

    private static string Plan(JsonElement element, string field, string serviceId, string path) =>
        element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new CatalogValidationException($"Catalog service '{serviceId}': field '{path}.{field}' must be a string.");
}
