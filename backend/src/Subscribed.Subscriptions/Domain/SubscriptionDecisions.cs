using System.Globalization;

namespace Subscribed.Subscriptions.Domain;

/// <summary>Raw request fields, exactly as the client sent them. Omitted means null (D-10).</summary>
public sealed record AddInput(string? Name, string? Category, string? Notes, string? Currency, string? StartDate, string? EndDate, string? Amount, string? CadenceUnit, int? CadenceCount);

public sealed record UpdateInput(string? Name, string? Category, string? Notes, string? Currency, string? StartDate, string? EndDate);

public sealed record PriceInput(string? EffectiveFrom, string? Amount, string? CadenceUnit, int? CadenceCount);

/// <summary>Fields that passed the shape rules.</summary>
public sealed record NewSubscription(string Name, string? Category, string? Notes, string Currency, DateOnly StartDate, DateOnly? EndDate, decimal Amount, Cadence Cadence);

public sealed record DetailsUpdate(string Name, string? Category, string? Notes, string Currency, DateOnly StartDate, DateOnly? EndDate);

public sealed record PriceEntryChange(DateOnly EffectiveFrom, decimal Amount, Cadence Cadence);

/// <summary>
/// Commands to events. Each command runs in two steps so a rejected change applies nothing: <c>Parse*</c> checks the shape of
/// the fields; the decision then rehydrates a fresh state from the history plus its candidate events and checks the BR-015
/// invariants on that. A command emits events only for real changes, and an empty list is a no-op.
/// </summary>
public static class SubscriptionDecisions
{
    // ---- step 1: shape -------------------------------------------------------------------------------

    public static NewSubscription ParseAdd(AddInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var errors = new FieldErrors();
        var name = Name(input.Name, errors);
        var category = Category(input.Category, errors);
        var notes = Notes(input.Notes, errors);
        var currency = Currency(input.Currency, errors);
        var start = Date(input.StartDate, "startDate", true, errors);
        var end = Date(input.EndDate, "endDate", false, errors);
        var (amount, cadence) = PriceFields(input.Amount, input.CadenceUnit, input.CadenceCount, currency, errors);
        errors.ThrowIfAny();
        return new NewSubscription(name!, category, notes, currency!, start!.Value, end, amount!.Value, cadence!.Value);
    }

    public static DetailsUpdate ParseUpdate(UpdateInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var errors = new FieldErrors();
        var name = Name(input.Name, errors);
        var category = Category(input.Category, errors);
        var notes = Notes(input.Notes, errors);
        var currency = Currency(input.Currency, errors);
        var start = Date(input.StartDate, "startDate", true, errors);
        var end = Date(input.EndDate, "endDate", false, errors);
        errors.ThrowIfAny();
        return new DetailsUpdate(name!, category, notes, currency!, start!.Value, end);
    }

    /// <summary>Shape only. The minor-unit rule needs the subscription's currency and runs with the invariants.</summary>
    public static PriceEntryChange ParsePriceEntry(PriceInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var errors = new FieldErrors();
        var from = Date(input.EffectiveFrom, "effectiveFrom", true, errors);
        var (amount, cadence) = PriceFields(input.Amount, input.CadenceUnit, input.CadenceCount, null, errors);
        errors.ThrowIfAny();
        return new PriceEntryChange(from!.Value, amount!.Value, cadence!.Value);
    }

    // ---- step 2: decisions ---------------------------------------------------------------------------

    public static IReadOnlyList<object> Add(NewSubscription command, string householdId, string entryId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(command);
        var added = new SubscriptionAdded(householdId, command.Name, command.Category, command.Notes, command.Currency, command.StartDate, command.EndDate,
            entryId, command.Amount, command.Cadence.Unit, command.Cadence.Count, now.ToUniversalTime());
        var candidate = SubscriptionState.Rehydrate([added]);
        var errors = new FieldErrors();
        if (candidate.EndDate is { } end && end < candidate.StartDate)
        {
            errors.Add("endDate", "The end date cannot be before the start date.");
        }

        errors.ThrowIfAny();
        return [added];
    }

    public static IReadOnlyList<object> UpdateDetails(IReadOnlyList<object> history, DetailsUpdate command, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(command);
        var current = Load(history);
        var at = now.ToUniversalTime();
        var events = new List<object>();
        if (!string.Equals(command.Name, current.Name, StringComparison.Ordinal)
            || !string.Equals(command.Category, current.Category, StringComparison.Ordinal)
            || !string.Equals(command.Notes, current.Notes, StringComparison.Ordinal))
        {
            events.Add(new SubscriptionDetailsChanged(command.Name, command.Category, command.Notes, at));
        }

        if (!string.Equals(command.Currency, current.Currency, StringComparison.Ordinal))
        {
            events.Add(new SubscriptionCurrencyCorrected(command.Currency, at));
        }

        if (command.StartDate != current.StartDate || command.EndDate != current.EndDate)
        {
            events.Add(new SubscriptionPeriodChanged(command.StartDate, command.EndDate, at));
        }

        if (events.Count == 0)
        {
            return [];
        }

        var candidate = Candidate(history, events);
        var errors = new FieldErrors();
        if (candidate.EndDate is { } end && end < candidate.StartDate)
        {
            errors.Add("endDate", "The end date cannot be before the start date.");
        }

        if (candidate.Entries.Any(e => !IsInitial(candidate, e) && e.EffectiveFrom <= candidate.StartDate))
        {
            errors.Add("startDate", "The start date must be before every later price entry.");
        }

        if (candidate.EndDate is { } last && candidate.Entries.Any(e => e.EffectiveFrom > last) && !errors.Has("endDate"))
        {
            errors.Add("endDate", "The end date cannot be before a price entry.");
        }

        MinorUnit(candidate.Currency, out var minor);
        if (candidate.Entries.Any(e => !Money.IsWholeMinorUnits(e.Amount, minor)))
        {
            errors.Add("currency", "An existing price is not a whole number of this currency's minor units.");
        }

        errors.ThrowIfAny();
        return events;
    }

    public static IReadOnlyList<object> AddPriceEntry(IReadOnlyList<object> history, PriceEntryChange command, string entryId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(command);
        var current = Load(history);
        var errors = new FieldErrors();
        CheckEntry(current, command, null, errors);
        errors.ThrowIfAny();
        return [new SubscriptionPriceEntryAdded(entryId, command.EffectiveFrom, command.Amount, command.Cadence.Unit, command.Cadence.Count, now.ToUniversalTime())];
    }

    public static IReadOnlyList<object> CorrectPriceEntry(IReadOnlyList<object> history, string entryId, PriceEntryChange command, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(command);
        var current = Load(history);
        var entry = current.Entries.FirstOrDefault(e => string.Equals(e.EntryId, entryId, StringComparison.Ordinal)) ?? throw new SubscriptionNotFoundException();
        var errors = new FieldErrors();
        CheckEntry(current, command, entry, errors);
        errors.ThrowIfAny();
        if (entry.EffectiveFrom == command.EffectiveFrom && entry.Amount == command.Amount && entry.Cadence == command.Cadence)
        {
            return [];
        }

        return [new SubscriptionPriceEntryCorrected(entryId, command.EffectiveFrom, command.Amount, command.Cadence.Unit, command.Cadence.Count, now.ToUniversalTime())];
    }

    public static IReadOnlyList<object> RemovePriceEntry(IReadOnlyList<object> history, string entryId, DateTimeOffset now)
    {
        var current = Load(history);
        if (!current.Entries.Any(e => string.Equals(e.EntryId, entryId, StringComparison.Ordinal)))
        {
            throw new SubscriptionNotFoundException();
        }

        if (string.Equals(entryId, current.InitialEntryId, StringComparison.Ordinal))
        {
            var errors = new FieldErrors();
            errors.Add("entryId", "The initial price entry cannot be removed.");
            errors.ThrowIfAny();
        }

        return [new SubscriptionPriceEntryRemoved(entryId, now.ToUniversalTime())];
    }

    public static IReadOnlyList<object> Remove(IReadOnlyList<object> history, DateTimeOffset now)
    {
        _ = Load(history);
        return [new SubscriptionRemoved(now.ToUniversalTime())];
    }

    // ---- helpers -------------------------------------------------------------------------------------

    private static void CheckEntry(SubscriptionState state, PriceEntryChange command, PriceEntryState? correcting, FieldErrors errors)
    {
        var initial = correcting is not null && IsInitial(state, correcting);
        if (initial)
        {
            if (command.EffectiveFrom != state.StartDate)
            {
                errors.Add("effectiveFrom", "The initial price entry must stay on the start date.");
            }
        }
        else
        {
            if (command.EffectiveFrom <= state.StartDate)
            {
                errors.Add("effectiveFrom", "The price must take effect after the start date.");
            }
            else if (state.EndDate is { } end && command.EffectiveFrom > end)
            {
                errors.Add("effectiveFrom", "The price cannot take effect after the end date.");
            }
            else if (state.Entries.Any(e => e != correcting && e.EffectiveFrom == command.EffectiveFrom))
            {
                errors.Add("effectiveFrom", "Another price already takes effect on this date.");
            }
        }

        MinorUnit(state.Currency, out var minor);
        if (!Money.HasAtMostFractionDigits(command.Amount, minor))
        {
            errors.Add("amount", $"The amount may have at most {minor} fraction digits in {state.Currency}.");
        }
    }

    private static bool IsInitial(SubscriptionState state, PriceEntryState entry) =>
        string.Equals(entry.EntryId, state.InitialEntryId, StringComparison.Ordinal);

    private static SubscriptionState Load(IReadOnlyList<object> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (history.Count == 0)
        {
            throw new SubscriptionNotFoundException();
        }

        var state = SubscriptionState.Rehydrate(history);
        return state.Removed ? throw new SubscriptionNotFoundException() : state;
    }

    /// <summary>A fresh rehydration of the history plus the candidate events. The live state is never copied or touched.</summary>
    private static SubscriptionState Candidate(IReadOnlyList<object> history, List<object> events) =>
        SubscriptionState.Rehydrate(history.Concat(events));

    private static void MinorUnit(string currency, out int minor)
    {
        if (!Iso4217.TryGetMinorUnit(currency, out minor))
        {
            throw new InvalidOperationException($"Stored currency '{currency}' is not in the ISO 4217 table.");
        }
    }

    private static string? Name(string? raw, FieldErrors errors)
    {
        if (SubscriptionText.TryNormalizeName(raw, out var name))
        {
            return name;
        }

        errors.Add("name", $"Name must be 1–{SubscriptionText.MaxName} characters with no control characters.");
        return null;
    }

    private static string? Category(string? raw, FieldErrors errors)
    {
        if (SubscriptionText.TryNormalizeCategory(raw, out var category))
        {
            return category;
        }

        errors.Add("category", $"Category must be at most {SubscriptionText.MaxCategory} characters with no control characters.");
        return null;
    }

    private static string? Notes(string? raw, FieldErrors errors)
    {
        if (SubscriptionText.TryNormalizeNotes(raw, out var notes))
        {
            return notes;
        }

        errors.Add("notes", $"Notes must be at most {SubscriptionText.MaxNotes} characters, with line feeds as the only control characters.");
        return null;
    }

    private static string? Currency(string? raw, FieldErrors errors)
    {
        if (Iso4217.IsValid(raw))
        {
            return raw;
        }

        errors.Add("currency", "Currency must be a three-letter ISO 4217 code in capitals.");
        return null;
    }

    private static DateOnly? Date(string? raw, string field, bool required, FieldErrors errors)
    {
        if (raw is null)
        {
            if (required)
            {
                errors.Add(field, "A date in yyyy-MM-dd format is required.");
            }

            return null;
        }

        if (DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date;
        }

        errors.Add(field, "The date must be in yyyy-MM-dd format.");
        return null;
    }

    private static (decimal? Amount, Cadence? Cadence) PriceFields(string? amountText, string? unit, int? count, string? currency, FieldErrors errors)
    {
        decimal? amount = null;
        if (amountText is null)
        {
            errors.Add("amount", "An amount is required.");
        }
        else if (!Money.TryParseAmount(amountText, out var parsed))
        {
            errors.Add("amount", "The amount must be a decimal with a dot, from 0 to 999999999.999.");
        }
        else if (currency is not null && Iso4217.TryGetMinorUnit(currency, out var minor) && !Money.HasAtMostFractionDigits(parsed, minor))
        {
            errors.Add("amount", $"The amount may have at most {minor} fraction digits in {currency}.");
        }
        else
        {
            amount = parsed;
        }

        Cadence? cadence = null;
        var unitOk = Cadence.IsValidUnit(unit);
        var countOk = Cadence.IsValidCount(count);
        if (!unitOk)
        {
            errors.Add("cadenceUnit", "The cadence unit must be week, month or year.");
        }

        if (!countOk)
        {
            errors.Add("cadenceCount", $"The cadence count must be a whole number from 1 to {Cadence.MaxCount}.");
        }

        if (unitOk && countOk)
        {
            cadence = new Cadence(unit!, count!.Value);
        }

        return (amount, cadence);
    }
}
