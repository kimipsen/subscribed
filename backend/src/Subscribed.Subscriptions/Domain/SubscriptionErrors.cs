namespace Subscribed.Subscriptions.Domain;

/// <summary>One or more field errors, keyed by camelCase field name. Messages are English and never shown by the UI.</summary>
public sealed class SubscriptionValidationException(IReadOnlyDictionary<string, string[]> errors) : Exception("Validation failed")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
}

/// <summary>Unknown, malformed or removed subscriptions, and unknown price-entry ids.</summary>
public sealed class SubscriptionNotFoundException() : Exception("Subscription not found.");

internal sealed class FieldErrors
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    public bool Any => _errors.Count > 0;

    public bool Has(string field) => _errors.ContainsKey(field);

    public void Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var list))
        {
            _errors[field] = list = [];
        }

        list.Add(message);
    }

    public void ThrowIfAny()
    {
        if (Any)
        {
            throw new SubscriptionValidationException(_errors.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.Ordinal));
        }
    }
}
