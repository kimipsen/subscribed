namespace Subscribed.Subscriptions.PaymentOptions;

/// <summary>Raw request fields. Omitted means null.</summary>
public sealed record PaymentOptionInput(string? Name, string? Label, string? Brand = null);

/// <summary>Fields that passed the shape rules.</summary>
public sealed record PaymentOptionFields(string Name, string? Label, string? Brand);

/// <summary>Unknown, malformed, removed or other-household payment options. Maps to HTTP 404.</summary>
public sealed class PaymentOptionNotFoundException() : Exception("Payment option not found.");

/// <summary>
/// A rejected name, label or brand. <see cref="Errors"/> are fixed strings and <see cref="Reasons"/> are fixed reason codes: neither
/// ever contains the input (NFR-2).
/// </summary>
public sealed class PaymentOptionRejectedException(IReadOnlyDictionary<string, string[]> errors, IReadOnlyList<string> reasons) : Exception("Validation failed")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;

    public IReadOnlyList<string> Reasons { get; } = reasons;
}

/// <summary>Commands to events, in two steps like <c>SubscriptionDecisions</c>: shape first, then decide. An empty list is a no-op.</summary>
public static class PaymentOptionDecisions
{
    public static PaymentOptionFields ParseCreate(PaymentOptionInput input) => Parse(input);

    public static PaymentOptionFields ParseUpdate(PaymentOptionInput input) => Parse(input);

    public static IReadOnlyList<object> Create(PaymentOptionFields command, string householdId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(command);
        return [new PaymentOptionCreated(householdId, command.Name, command.Label, now.ToUniversalTime(), command.Brand)];
    }

    /// <summary>No event when name, label and brand are all unchanged. A removed option is not found (404), matching <see cref="Remove"/>.</summary>
    public static IReadOnlyList<object> Change(IReadOnlyList<object> history, PaymentOptionFields command, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(command);
        var current = Load(history);
        if (string.Equals(command.Name, current.Name, StringComparison.Ordinal)
            && string.Equals(command.Label, current.Label, StringComparison.Ordinal)
            && string.Equals(command.Brand, current.Brand, StringComparison.Ordinal))
        {
            return [];
        }

        return [new PaymentOptionChanged(command.Name, command.Label, now.ToUniversalTime(), command.Brand)];
    }

    /// <summary>Soft remove. An already-removed option is not found, so a double remove is a 404.</summary>
    public static IReadOnlyList<object> Remove(IReadOnlyList<object> history, DateTimeOffset now)
    {
        _ = Load(history);
        return [new PaymentOptionRemoved(now.ToUniversalTime())];
    }

    private static PaymentOptionFields Parse(PaymentOptionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var reasons = new List<string>();
        var nameOk = PaymentOptionText.TryNormalizeName(input.Name, out var name, out var nameReason);
        if (!nameOk)
        {
            errors["name"] = [PaymentOptionText.MessageFor("name", nameReason)];
            reasons.Add(nameReason);
        }

        var labelOk = PaymentOptionText.TryNormalizeLabel(input.Label, out var label, out var labelReason);
        if (!labelOk)
        {
            errors["label"] = [PaymentOptionText.MessageFor("label", labelReason)];
            reasons.Add(labelReason);
        }

        if (!PaymentOptionText.TryValidateBrand(input.Brand, out var brandReason))
        {
            errors["brand"] = [PaymentOptionText.MessageFor("brand", brandReason)];
            reasons.Add(brandReason);
        }

        return errors.Count > 0 ? throw new PaymentOptionRejectedException(errors, reasons) : new PaymentOptionFields(name, label, input.Brand);
    }

    private static PaymentOptionState Load(IReadOnlyList<object> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (history.Count == 0)
        {
            throw new PaymentOptionNotFoundException();
        }

        var state = PaymentOptionState.Rehydrate(history);
        return state.Removed ? throw new PaymentOptionNotFoundException() : state;
    }
}
