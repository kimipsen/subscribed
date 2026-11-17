namespace Subscribed.Subscriptions.PaymentOptions;

/// <summary>Create and update body. Every property is nullable: omitted means null, so a missing field is reported under its own key. PUT is a full replace: an omitted or null brand clears it.</summary>
public sealed record PaymentOptionRequest(string? Name, string? Label, string? Brand)
{
    public PaymentOptionInput ToInput() => new(Name, Label, Brand);
}

/// <summary>A removed option stays in the list with <c>removed: true</c>, so price entries that reference it keep resolving by id.</summary>
public sealed record PaymentOptionDto(string Id, long Version, string Name, string? Label, string? Brand, bool Removed)
{
    public static PaymentOptionDto From(PaymentOptionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return new PaymentOptionDto(PaymentOptionIds.Bare(state.Id), state.Version, state.Name, state.Label, state.Brand, state.Removed);
    }
}

public sealed record PaymentOptionListDto(IReadOnlyList<PaymentOptionDto> Options);

public sealed record PaymentOptionSuggestionDto(string Id, string Name);

public sealed record PaymentOptionSuggestionsDto(IReadOnlyList<PaymentOptionSuggestionDto> Suggestions)
{
    public static PaymentOptionSuggestionsDto Default { get; } = new([.. PaymentOptionSuggestions.All.Select(static s => new PaymentOptionSuggestionDto(s.Id, s.Name))]);
}
