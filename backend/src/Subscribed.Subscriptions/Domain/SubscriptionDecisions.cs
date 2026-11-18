using System.Globalization;
using Subscribed.Subscriptions.PaymentOptions;

namespace Subscribed.Subscriptions.Domain;

/// <summary>Raw request fields, exactly as the client sent them. Omitted means null (D-10).</summary>
public sealed record AddInput(string? Name, string? Category, string? Notes, string? Currency, string? StartDate, string? EndDate, string? Amount, string? CadenceUnit, int? CadenceCount, string? CatalogId = null, string? UnsubscribeUrl = null, string? PaymentOptionId = null, string? CancelBy = null);

/// <summary>
/// <c>PaymentOptionSet</c> is true when the request carried a <c>paymentOption</c> object; <c>PaymentOptionIdSpecified</c> is true when
/// that object had an <c>id</c> property (null included). An omitted object leaves the price entries alone.
/// </summary>
public sealed record UpdateInput(string? Name, string? Category, string? Notes, string? Currency, string? StartDate, string? EndDate, string? CatalogId = null, string? UnsubscribeUrl = null, bool PaymentOptionSet = false, bool PaymentOptionIdSpecified = false, string? PaymentOptionId = null, string? CancelBy = null);

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
/// <summary>The option the Edit form asks for from today on. A null id means None.</summary>
public sealed record PaymentOptionChange(string? PaymentOptionId);

public sealed record DetailsUpdate(string Name, string? Category, string? Notes, string Currency, DateOnly StartDate, DateOnly? EndDate, string? CatalogId = null, string? UnsubscribeUrl = null, PaymentOptionChange? PaymentOption = null, DateTimeOffset? CancelBy = null, string? CancelByLocal = null, TimeZoneInfo? Zone = null);

public sealed record PriceEntryChange(DateOnly EffectiveFrom, decimal Amount, Cadence Cadence, string? PaymentOptionId = null);

/// <summary>Raw pause period fields, exactly as the client sent them. Omitted means null.</summary>
public sealed record PauseInput(string? PauseDate, string? ResumeDate);

/// <summary>A pause period that passed the shape rules. A null <paramref name="ResumeDate"/> is an open period.</summary>
public sealed record PauseChange(DateOnly PauseDate, DateOnly? ResumeDate);

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
        PaymentOptionChange? paymentOption = null;
        if (input.PaymentOptionSet)
        {
            // An object without an id would bind like { "id": null } and silently clear the option, so it is rejected instead.
            if (!input.PaymentOptionIdSpecified)
            {
                errors.Add("paymentOption", "The payment option must have an id, or null for none.");
            }
            else
            {
                paymentOption = new PaymentOptionChange(PaymentOptionId(input.PaymentOptionId, errors));
            }
        }

        errors.ThrowIfAny();
        return new DetailsUpdate(name!, category, notes, currency!, start!.Value, end, catalogId, unsubscribeUrl, paymentOption, cancelBy, cancelByLocal, zone);
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

    /// <summary>Shape only: <c>pauseDate</c> is required, <c>resumeDate</c> optional, both <c>yyyy-MM-dd</c>. The invariants run with the decision.</summary>
    public static PauseChange ParsePause(PauseInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var errors = new FieldErrors();
        var pause = Date(input.PauseDate, "pauseDate", true, errors);
        var resume = Date(input.ResumeDate, "resumeDate", false, errors);
        errors.ThrowIfAny();
        return new PauseChange(pause!.Value, resume);
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

    /// <summary>For callers with no payment option change. A throwaway entry id is never used then.</summary>
    public static IReadOnlyList<object> UpdateDetails(IReadOnlyList<object> history, DetailsUpdate command, DateTimeOffset now) =>
        UpdateDetails(history, command, Guid.CreateVersion7(now).ToString("N"), now);

    /// <summary>
    /// Details and the optional payment option change in one decision, so one append: a partial change cannot happen. The payment
    /// events are derived from the candidate that already contains the detail events, because the same save can move the period.
    /// </summary>
    public static IReadOnlyList<object> UpdateDetails(IReadOnlyList<object> history, DetailsUpdate command, string newEntryId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(newEntryId);
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

        var errors = new FieldErrors();
        if (command.PaymentOption is { } change)
        {
            events.AddRange(PaymentEvents(Candidate(history, events), change, newEntryId, at, errors));
        }

        if (events.Count == 0)
        {
            errors.ThrowIfAny();
            return [];
        }

        var candidate = Candidate(history, events);
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

    /// <summary>
    /// The price entry events for "this option from today". Judged against the current entry only (upcoming entries are not
    /// compared). The current entry is updated in place when it starts today or later, otherwise a new entry starting today copies
    /// its amount and cadence. Upcoming entries other than the one handled as current get the option too. An ended subscription is
    /// rejected, but only when the option really differs, so an unchanged option never blocks the details save.
    /// </summary>
    private static List<object> PaymentEvents(SubscriptionState candidate, PaymentOptionChange change, string newEntryId, DateTimeOffset at, FieldErrors errors)
    {
        var today = DateOnly.FromDateTime(at.UtcDateTime);
        var current = SubscriptionCalculator.CurrentPrice(candidate, today);
        var events = new List<object>();
        if (string.Equals(current.PaymentOptionId, change.PaymentOptionId, StringComparison.Ordinal))
        {
            return events;
        }

        if (SubscriptionCalculator.IsEnded(candidate, today))
        {
            errors.Add("paymentOption", "The payment option cannot be changed on an ended subscription.");
            return events;
        }

        if (current.EffectiveFrom >= today)
        {
            events.Add(new SubscriptionPriceEntryPaymentChanged(current.EntryId, change.PaymentOptionId, at));
        }
        else
        {
            events.Add(new SubscriptionPriceEntryAdded(newEntryId, today, current.Amount, current.CadenceUnit, current.CadenceCount, at));
            if (change.PaymentOptionId is not null)
            {
                events.Add(new SubscriptionPriceEntryPaymentChanged(newEntryId, change.PaymentOptionId, at));
            }
        }

        foreach (var upcoming in candidate.Entries
                     .Where(e => e.EffectiveFrom > today && !string.Equals(e.EntryId, current.EntryId, StringComparison.Ordinal))
                     .OrderBy(e => e.EffectiveFrom))
        {
            if (!string.Equals(upcoming.PaymentOptionId, change.PaymentOptionId, StringComparison.Ordinal))
            {
                events.Add(new SubscriptionPriceEntryPaymentChanged(upcoming.EntryId, change.PaymentOptionId, at));
            }
        }

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

    /// <summary>
    /// Opens a pause period that starts today. Allowed in any non-removed state; pausing a paused subscription is rejected with
    /// <see cref="SubscriptionStateException"/>, not a no-op. <paramref name="zone"/> resolves legacy periods (D11).
    /// </summary>
    public static IReadOnlyList<object> Pause(IReadOnlyList<object> history, string periodId, DateOnly today, TimeZoneInfo zone, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(periodId);
        var periods = Load(history).ResolvePausePeriods(zone, today);
        return SubscriptionCalculator.IsPausedOn(periods, today)
            ? throw SubscriptionStateException.AlreadyPaused()
            : [new SubscriptionPausePeriodAdded(periodId, today, null, now.ToUniversalTime())];
    }

    /// <summary>
    /// Closes the open period that contains today with today as the resume date. A period that started today would become zero
    /// days long (resume must be after pause), so it is removed instead (D6). Nothing to close is a <see cref="SubscriptionStateException"/>.
    /// </summary>
    public static IReadOnlyList<object> Resume(IReadOnlyList<object> history, DateOnly today, TimeZoneInfo zone, DateTimeOffset now)
    {
        var periods = Load(history).ResolvePausePeriods(zone, today);
        var open = periods.FirstOrDefault(p => p.ResumeDate is null && p.PauseDate <= today) ?? throw SubscriptionStateException.NotPaused();
        var at = now.ToUniversalTime();
        return open.PauseDate == today
            ? [new SubscriptionPausePeriodRemoved(open.PeriodId, at)]
            : [new SubscriptionPausePeriodChanged(open.PeriodId, open.PauseDate, today, at)];
    }

    public static IReadOnlyList<object> AddPausePeriod(IReadOnlyList<object> history, PauseChange command, string periodId, DateOnly today, TimeZoneInfo zone, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(periodId);
        var periods = Load(history).ResolvePausePeriods(zone, today);
        var errors = new FieldErrors();
        CheckPausePeriod(periods, command, today, errors);
        errors.ThrowIfAny();
        return [new SubscriptionPausePeriodAdded(periodId, command.PauseDate, command.ResumeDate, now.ToUniversalTime())];
    }

    /// <summary>An unknown id (or a legacy period dropped as zero-day, D7) is <see cref="SubscriptionNotFoundException"/>. The period itself is ignored by the overlap check.</summary>
    public static IReadOnlyList<object> ChangePausePeriod(IReadOnlyList<object> history, string periodId, PauseChange command, DateOnly today, TimeZoneInfo zone, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(command);
        var periods = Load(history).ResolvePausePeriods(zone, today);
        var target = periods.FirstOrDefault(p => string.Equals(p.PeriodId, periodId, StringComparison.Ordinal)) ?? throw new SubscriptionNotFoundException();
        var errors = new FieldErrors();
        CheckPausePeriod([.. periods.Where(p => !ReferenceEquals(p, target))], command, today, errors);
        errors.ThrowIfAny();
        return target.PauseDate == command.PauseDate && target.ResumeDate == command.ResumeDate
            ? []
            : [new SubscriptionPausePeriodChanged(periodId, command.PauseDate, command.ResumeDate, now.ToUniversalTime())];
    }

    public static IReadOnlyList<object> RemovePausePeriod(IReadOnlyList<object> history, string periodId, DateOnly today, TimeZoneInfo zone, DateTimeOffset now)
    {
        var periods = Load(history).ResolvePausePeriods(zone, today);
        return periods.Any(p => string.Equals(p.PeriodId, periodId, StringComparison.Ordinal))
            ? [new SubscriptionPausePeriodRemoved(periodId, now.ToUniversalTime())]
            : throw new SubscriptionNotFoundException();
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

    /// <summary>
    /// The one invariant check for add and change (R5.5, R5.7). <paramref name="others"/> are the resolved periods the new one is
    /// compared with. Periods may start before the subscription start or end after its end (R5.6), so the subscription dates are not checked.
    /// </summary>
    private static void CheckPausePeriod(IReadOnlyList<PausePeriod> others, PauseChange change, DateOnly today, FieldErrors errors)
    {
        if (change.PauseDate > today)
        {
            errors.Add("pauseDate", "The pause date cannot be in the future.");
        }

        if (change.ResumeDate is { } future && future > today)
        {
            errors.Add("resumeDate", "The resume date cannot be in the future.");
        }

        if (change.ResumeDate is { } resume && resume <= change.PauseDate)
        {
            errors.Add("resumeDate", "The resume date must be after the pause date.");
            return;
        }

        var end = change.ResumeDate ?? DateOnly.MaxValue;
        if (others.Any(o => change.PauseDate < (o.ResumeDate ?? DateOnly.MaxValue) && o.PauseDate < end))
        {
            errors.Add("pauseDate", "The pause overlaps another pause.");
        }

        if (change.ResumeDate is null && others.Any(o => o.ResumeDate is null || o.PauseDate >= change.PauseDate))
        {
            errors.Add("resumeDate", "Only the latest pause can be open.");
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
