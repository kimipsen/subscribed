using System.Globalization;
using Subscribed.Subscriptions.PaymentOptions;

namespace Subscribed.Subscriptions.Domain;

/// <summary>Raw request fields, exactly as the client sent them. Omitted means null (D-10).</summary>
public sealed record AddInput(string? Name, string? Category, string? Notes, string? Currency, string? StartDate, string? EndDate, string? Amount, string? CadenceUnit, int? CadenceCount, string? CatalogId = null, string? UnsubscribeUrl = null, string? PaymentOptionId = null, string? CancelBy = null);

public sealed record UpdateInput(string? Name, string? Category, string? Notes, string? Currency, string? StartDate, string? EndDate, string? CatalogId = null, string? UnsubscribeUrl = null, string? CancelBy = null);

public sealed record PriceInput(string? EffectiveFrom, string? Amount, string? CadenceUnit, int? CadenceCount, string? PaymentOptionId = null);

/// <summary>
/// Fields that passed the shape rules. <paramref name="CancelBy"/> is the parsed UTC instant; <paramref name="Zone"/> is the
/// household zone it was parsed in, and the zone the date rule (D5) is checked in. A null zone means UTC.
/// </summary>
public sealed record NewSubscription(string Name, string? Category, string? Notes, string Currency, DateOnly StartDate, DateOnly? EndDate, decimal Amount, Cadence Cadence, string? CatalogId = null, string? UnsubscribeUrl = null, string? PaymentOptionId = null, DateTimeOffset? CancelBy = null, TimeZoneInfo? Zone = null);

/// <summary>
/// <paramref name="CancelByLocal"/> is the submitted deadline as a normalised local string (<c>yyyy-MM-ddTHH:mm</c>, with
/// <c>:ss</c> only when the seconds are not zero), or null. The update decision compares it with the current local rendering
/// of the stored instant, never instant with instant. A null zone means UTC.
/// </summary>
public sealed record DetailsUpdate(string Name, string? Category, string? Notes, string Currency, DateOnly StartDate, DateOnly? EndDate, string? CatalogId = null, string? UnsubscribeUrl = null, DateTimeOffset? CancelBy = null, string? CancelByLocal = null, TimeZoneInfo? Zone = null);

public sealed record PriceEntryChange(DateOnly EffectiveFrom, decimal Amount, Cadence Cadence, string? PaymentOptionId = null);

/// <summary>
/// Commands to events. Each command runs in two steps so a rejected change applies nothing: <c>Parse*</c> checks the shape of
/// the fields; the decision then rehydrates a fresh state from the history plus its candidate events and checks the BR-015
/// invariants on that. A command emits events only for real changes, and an empty list is a no-op.
/// </summary>
public static class SubscriptionDecisions
{
    // ---- step 1: shape -------------------------------------------------------------------------------

    /// <summary>The zone is required, with no default, so a call site cannot forget the household zone.</summary>
    public static NewSubscription ParseAdd(AddInput input, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(zone);
        var errors = new FieldErrors();
        var name = Name(input.Name, errors);
        var category = Category(input.Category, errors);
        var notes = Notes(input.Notes, errors);
        var currency = Currency(input.Currency, errors);
        var start = Date(input.StartDate, "startDate", true, errors);
        var end = Date(input.EndDate, "endDate", false, errors);
        var (amount, cadence) = PriceFields(input.Amount, input.CadenceUnit, input.CadenceCount, currency, errors);
        var catalogId = CatalogId(input.CatalogId, errors);
        var paymentOptionId = PaymentOptionId(input.PaymentOptionId, errors);
        var unsubscribeUrl = UnsubscribeUrl(input.UnsubscribeUrl, errors);
        var (cancelBy, _) = CancelBy(input.CancelBy, zone, errors);
        errors.ThrowIfAny();
        return new NewSubscription(name!, category, notes, currency!, start!.Value, end, amount!.Value, cadence!.Value, catalogId, unsubscribeUrl, paymentOptionId, cancelBy, zone);
    }

    /// <summary>The zone is required, with no default, so a call site cannot forget the household zone.</summary>
    public static DetailsUpdate ParseUpdate(UpdateInput input, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(zone);
        var errors = new FieldErrors();
        var name = Name(input.Name, errors);
        var category = Category(input.Category, errors);
        var notes = Notes(input.Notes, errors);
        var currency = Currency(input.Currency, errors);
        var start = Date(input.StartDate, "startDate", true, errors);
        var end = Date(input.EndDate, "endDate", false, errors);
        var catalogId = CatalogId(input.CatalogId, errors);
        var unsubscribeUrl = UnsubscribeUrl(input.UnsubscribeUrl, errors);
        var (cancelBy, cancelByLocal) = CancelBy(input.CancelBy, zone, errors);
        errors.ThrowIfAny();
        return new DetailsUpdate(name!, category, notes, currency!, start!.Value, end, catalogId, unsubscribeUrl, cancelBy, cancelByLocal, zone);
    }

    /// <summary>Shape only. The minor-unit rule needs the subscription's currency and runs with the invariants.</summary>
    public static PriceEntryChange ParsePriceEntry(PriceInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var errors = new FieldErrors();
        var from = Date(input.EffectiveFrom, "effectiveFrom", true, errors);
        var (amount, cadence) = PriceFields(input.Amount, input.CadenceUnit, input.CadenceCount, null, errors);
        var paymentOptionId = PaymentOptionId(input.PaymentOptionId, errors);
        errors.ThrowIfAny();
        return new PriceEntryChange(from!.Value, amount!.Value, cadence!.Value, paymentOptionId);
    }

    // ---- step 2: decisions ---------------------------------------------------------------------------

    public static IReadOnlyList<object> Add(NewSubscription command, string householdId, string entryId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(command);
        var added = new SubscriptionAdded(householdId, command.Name, command.Category, command.Notes, command.Currency, command.StartDate, command.EndDate,
            entryId, command.Amount, command.Cadence.Unit, command.Cadence.Count, now.ToUniversalTime(), command.UnsubscribeUrl);
        var candidate = SubscriptionState.Rehydrate([added]);
        var errors = new FieldErrors();
        if (candidate.EndDate is { } end && end < candidate.StartDate)
        {
            errors.Add("endDate", "The end date cannot be before the start date.");
        }

        if (command.CancelBy is { } cancelBy && LocalDate(cancelBy, command.Zone) < candidate.StartDate)
        {
            errors.Add("cancelBy", SubscriptionText.CancelByBeforeStart);
        }

        errors.ThrowIfAny();
        var events = new List<object> { added };
        if (command.CancelBy is not null)
        {
            events.Add(new SubscriptionCancelByChanged(command.CancelBy.Value.ToUniversalTime(), now.ToUniversalTime()));
        }

        if (command.CatalogId is not null)
        {
            events.Add(new SubscriptionCatalogLinked(command.CatalogId, now.ToUniversalTime()));
        }

        if (command.PaymentOptionId is not null)
        {
            events.Add(new SubscriptionPriceEntryPaymentChanged(entryId, command.PaymentOptionId, now.ToUniversalTime()));
        }

        return events;
    }

    public static IReadOnlyList<object> UpdateDetails(IReadOnlyList<object> history, DetailsUpdate command, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(command);
        var current = Load(history);
        var at = now.ToUniversalTime();
        var events = new List<object>();
        if (!string.Equals(command.Name, current.Name, StringComparison.Ordinal)
            || !string.Equals(command.Category, current.Category, StringComparison.Ordinal)
            || !string.Equals(command.Notes, current.Notes, StringComparison.Ordinal)
            || !string.Equals(command.UnsubscribeUrl, current.UnsubscribeUrl, StringComparison.Ordinal))
        {
            events.Add(new SubscriptionDetailsChanged(command.Name, command.Category, command.Notes, at, command.UnsubscribeUrl));
        }

        if (!string.Equals(command.Currency, current.Currency, StringComparison.Ordinal))
        {
            events.Add(new SubscriptionCurrencyCorrected(command.Currency, at));
        }

        if (command.StartDate != current.StartDate || command.EndDate != current.EndDate)
        {
            events.Add(new SubscriptionPeriodChanged(command.StartDate, command.EndDate, at));
        }

        if (!string.Equals(command.CatalogId, current.CatalogId, StringComparison.Ordinal))
        {
            events.Add(new SubscriptionCatalogLinked(command.CatalogId, at));
        }

        // Changed means the submitted local string differs from the current local rendering of the stored instant. Comparing
        // instants would let a save that did not touch the deadline move it (DST overlap, tz database change, household zone change).
        var cancelByChanged = !string.Equals(command.CancelByLocal, RenderCancelBy(current.CancelBy, command.Zone), StringComparison.Ordinal);
        if (cancelByChanged)
        {
            events.Add(new SubscriptionCancelByChanged(command.CancelBy?.ToUniversalTime(), at));
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

        // D5 runs only when the deadline or the start date is part of this change, so a rename is never blocked by a stale deadline.
        if ((cancelByChanged || command.StartDate != current.StartDate)
            && candidate.CancelBy is { } effective
            && LocalDate(effective, command.Zone) < candidate.StartDate)
        {
            errors.Add("cancelBy", SubscriptionText.CancelByBeforeStart);
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
        var at = now.ToUniversalTime();
        var events = new List<object> { new SubscriptionPriceEntryAdded(entryId, command.EffectiveFrom, command.Amount, command.Cadence.Unit, command.Cadence.Count, at) };
        if (command.PaymentOptionId is not null)
        {
            events.Add(new SubscriptionPriceEntryPaymentChanged(entryId, command.PaymentOptionId, at));
        }

        return events;
    }

    public static IReadOnlyList<object> CorrectPriceEntry(IReadOnlyList<object> history, string entryId, PriceEntryChange command, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(command);
        var current = Load(history);
        var entry = current.Entries.FirstOrDefault(e => string.Equals(e.EntryId, entryId, StringComparison.Ordinal)) ?? throw new SubscriptionNotFoundException();
        var errors = new FieldErrors();
        CheckEntry(current, command, entry, errors);
        errors.ThrowIfAny();
        var at = now.ToUniversalTime();
        var events = new List<object>();
        if (entry.EffectiveFrom != command.EffectiveFrom || entry.Amount != command.Amount || entry.Cadence != command.Cadence)
        {
            events.Add(new SubscriptionPriceEntryCorrected(entryId, command.EffectiveFrom, command.Amount, command.Cadence.Unit, command.Cadence.Count, at));
        }

        // A changed option, including non-null to null, is its own event. Neither differing means no events at all.
        if (!string.Equals(entry.PaymentOptionId, command.PaymentOptionId, StringComparison.Ordinal))
        {
            events.Add(new SubscriptionPriceEntryPaymentChanged(entryId, command.PaymentOptionId, at));
        }

        return events;
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

    /// <summary>Allowed in any non-removed state. Pausing a paused subscription is rejected, not a no-op.</summary>
    public static IReadOnlyList<object> Pause(IReadOnlyList<object> history, DateTimeOffset now)
    {
        var current = Load(history);
        return current.Paused ? throw SubscriptionStateException.AlreadyPaused() : [new SubscriptionPaused(now.ToUniversalTime())];
    }

    public static IReadOnlyList<object> Resume(IReadOnlyList<object> history, DateTimeOffset now)
    {
        var current = Load(history);
        return current.Paused ? [new SubscriptionResumed(now.ToUniversalTime())] : throw SubscriptionStateException.NotPaused();
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

    /// <summary>Trimmed; empty becomes null (no link). Otherwise the same shape as a catalog service id.</summary>
    private static string? CatalogId(string? raw, FieldErrors errors)
    {
        var trimmed = (raw ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (CatalogLoader.IsValidId(trimmed))
        {
            return trimmed;
        }

        errors.Add("catalogId", $"The catalog id must be lowercase letters, digits and single hyphens, at most {CatalogLoader.MaxIdLength} characters.");
        return null;
    }

    /// <summary>Trimmed; empty becomes null (no payment option). Otherwise the same 32-hex shape as every other id.</summary>
    private static string? PaymentOptionId(string? raw, FieldErrors errors)
    {
        var trimmed = (raw ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (PaymentOptionIds.TryParse(trimmed, out var id))
        {
            return id;
        }

        errors.Add("paymentOptionId", "The payment option id is not valid.");
        return null;
    }

    /// <summary>Trimmed; empty becomes null (no user link). Otherwise the shared https-only rule; the URL value is never reported or logged.</summary>
    private static string? UnsubscribeUrl(string? raw, FieldErrors errors)
    {
        if (SubscriptionText.TryNormalizeUnsubscribeUrl(raw, out var url))
        {
            return url;
        }

        errors.Add("unsubscribeUrl", $"The unsubscribe link must be an https:// URL of at most {SubscriptionText.MaxUnsubscribeUrl} characters, with no spaces, backslashes or user-info.");
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

    /// <summary>
    /// The local rendering of a stored deadline in the zone: <c>yyyy-MM-ddTHH:mm</c>, with <c>:ss</c> only when the seconds are not
    /// zero, or null for no deadline. A null zone means UTC. The API shows this string and the update decision compares against it.
    /// </summary>
    public static string? RenderCancelBy(DateTimeOffset? instant, TimeZoneInfo? zone) =>
        instant is { } value ? FormatLocal(TimeZoneInfo.ConvertTime(value, zone ?? TimeZoneInfo.Utc).DateTime) : null;

    private static string FormatLocal(DateTime local) =>
        local.ToString(local.Second == 0 ? "yyyy-MM-ddTHH:mm" : "yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    private static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo? zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone ?? TimeZoneInfo.Utc).DateTime);

    /// <summary>
    /// Parses a local wall time in the household zone (D3, D4). Null or empty means no deadline. Only <c>yyyy-MM-ddTHH:mm</c> and
    /// <c>yyyy-MM-ddTHH:mm:ss</c> are accepted, so an offset or <c>Z</c> is a format error. A time inside a DST gap is rejected;
    /// a time inside a DST overlap resolves to the earlier instant. The instant is returned in UTC with the normalised local string.
    /// </summary>
    private static (DateTimeOffset? Instant, string? Local) CancelBy(string? raw, TimeZoneInfo zone, FieldErrors errors)
    {
        var trimmed = (raw ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return (null, null);
        }

        if (!DateTime.TryParseExact(trimmed, ["yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            || local.Kind != DateTimeKind.Unspecified
            || trimmed.AsSpan(11, 2).SequenceEqual("24"))
        {
            errors.Add("cancelBy", SubscriptionText.CancelByFormat);
            return (null, null);
        }

        if (zone.IsInvalidTime(local))
        {
            errors.Add("cancelBy", SubscriptionText.CancelByGap);
            return (null, null);
        }

        // In an overlap the earlier instant has the larger UTC offset.
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        try
        {
            // Subtract the offset on the DateTime: the DateTimeOffset constructor can reject offsets that are not whole minutes (LMT).
            var utc = DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
            return (new DateTimeOffset(utc), FormatLocal(local));
        }
        catch (ArgumentOutOfRangeException)
        {
            errors.Add("cancelBy", SubscriptionText.CancelByFormat);
            return (null, null);
        }
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
