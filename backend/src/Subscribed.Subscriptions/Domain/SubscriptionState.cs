using System.Globalization;

namespace Subscribed.Subscriptions.Domain;

public sealed class PriceEntryState
{
    public string EntryId { get; set; } = "";

    public DateOnly EffectiveFrom { get; set; }

    public decimal Amount { get; set; }

    public string CadenceUnit { get; set; } = "";

    public int CadenceCount { get; set; }

    /// <summary>The payment option of this entry, or null. Snapshots written before this property existed read as null.</summary>
    public string? PaymentOptionId { get; set; }

    public Cadence Cadence => new(CadenceUnit, CadenceCount);
}

/// <summary>
/// One stored pause period. Explicit dates (new events) are kept as dates; a period rebuilt from the legacy
/// <see cref="SubscriptionPaused"/> / <see cref="SubscriptionResumed"/> events keeps the instants and is turned into dates in the
/// household zone by <see cref="SubscriptionState.ResolvePausePeriods"/>.
/// </summary>
public sealed class PausePeriodState
{
    public string PeriodId { get; set; } = "";

    /// <summary>Null while the date is still the legacy instant.</summary>
    public DateOnly? PauseDate { get; set; }

    public DateOnly? ResumeDate { get; set; }

    /// <summary>The legacy <see cref="SubscriptionPaused.RecordedAt"/>.</summary>
    public DateTimeOffset? PausedInstant { get; set; }

    /// <summary>The legacy <see cref="SubscriptionResumed.RecordedAt"/>.</summary>
    public DateTimeOffset? ResumedInstant { get; set; }

    internal bool IsOpenLegacy => PauseDate is null && PausedInstant is not null && ResumedInstant is null;
}

/// <summary>A resolved pause period: <c>[PauseDate, ResumeDate)</c>, open when <paramref name="ResumeDate"/> is null.</summary>
public sealed record PausePeriod(string PeriodId, DateOnly PauseDate, DateOnly? ResumeDate);

/// <summary>
/// The domain state and the Marten document (<c>subscription_view</c>) in one: a self-aggregating inline snapshot (D-12). Marten
/// finds <see cref="Create"/> and the <c>Apply</c> methods by convention; <see cref="Rehydrate"/> calls the same methods, so
/// there is one evolution implementation. The <c>Apply</c> methods never touch <see cref="Version"/>.
/// </summary>
public sealed class SubscriptionState
{
    internal const string LegacyPeriodIdPrefix = "legacy-";

    public string Id { get; set; } = "";

    public string HouseholdId { get; set; } = "";

    public string Name { get; set; } = "";

    public string? Category { get; set; }

    public string? Notes { get; set; }

    public string Currency { get; set; } = "";

    public DateOnly StartDate { get; set; }

    public DateOnly? EndDate { get; set; }

    public string InitialEntryId { get; set; } = "";

    /// <summary>The linked catalog entry, or null. Snapshots written before this property existed read as null.</summary>
    public string? CatalogId { get; set; }

    /// <summary>The user-specified unsubscribe link, or null. Snapshots written before this property existed read as null.</summary>
    public string? UnsubscribeUrl { get; set; }

    /// <summary>The cancel-by deadline as a UTC instant, or null. Snapshots written before this property existed read as null.</summary>
    public DateTimeOffset? CancelBy { get; set; }

    public List<PriceEntryState> Entries { get; set; } = [];

    public DateTimeOffset AddedAt { get; set; }

    public bool Removed { get; set; }

    public DateTimeOffset? RemovedAt { get; set; }

    /// <summary>
    /// The stored pause periods. Snapshots written before pause periods existed read as an empty list until the projection is
    /// rebuilt. The Paused status is derived per request from <see cref="ResolvePausePeriods"/>.
    /// </summary>
    public List<PausePeriodState> PausePeriods { get; set; } = [];

    /// <summary>The number of legacy <see cref="SubscriptionPaused"/> events applied. Never decremented, so a legacy id is never reused.</summary>
    public int LegacyPauseCount { get; set; }

    public long Version { get; set; }

    public static SubscriptionState Create(SubscriptionAdded e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return new SubscriptionState
        {
            HouseholdId = e.HouseholdId,
            Name = e.Name,
            Category = e.Category,
            Notes = e.Notes,
            UnsubscribeUrl = e.UnsubscribeUrl,
            Currency = e.Currency,
            StartDate = e.StartDate,
            EndDate = e.EndDate,
            InitialEntryId = e.InitialEntryId,
            AddedAt = e.RecordedAt,
            Entries =
            [
                new PriceEntryState
                {
                    EntryId = e.InitialEntryId,
                    EffectiveFrom = e.StartDate,
                    Amount = e.Amount,
                    CadenceUnit = e.CadenceUnit,
                    CadenceCount = e.CadenceCount,
                },
            ],
        };
    }

    public void Apply(SubscriptionDetailsChanged e)
    {
        ArgumentNullException.ThrowIfNull(e);
        Name = e.Name;
        Category = e.Category;
        Notes = e.Notes;
        UnsubscribeUrl = e.UnsubscribeUrl;
    }

    public void Apply(SubscriptionCurrencyCorrected e)
    {
        ArgumentNullException.ThrowIfNull(e);
        Currency = e.Currency;
    }

    /// <summary>Moves the initial entry to the new start date (BR-013).</summary>
    public void Apply(SubscriptionPeriodChanged e)
    {
        ArgumentNullException.ThrowIfNull(e);
        StartDate = e.StartDate;
        EndDate = e.EndDate;
        Entry(InitialEntryId).EffectiveFrom = e.StartDate;
    }

    public void Apply(SubscriptionPriceEntryAdded e)
    {
        ArgumentNullException.ThrowIfNull(e);
        Entries.Add(new PriceEntryState
        {
            EntryId = e.EntryId,
            EffectiveFrom = e.EffectiveFrom,
            Amount = e.Amount,
            CadenceUnit = e.CadenceUnit,
            CadenceCount = e.CadenceCount,
        });
    }

    public void Apply(SubscriptionPriceEntryCorrected e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var entry = Entry(e.EntryId);
        entry.EffectiveFrom = e.EffectiveFrom;
        entry.Amount = e.Amount;
        entry.CadenceUnit = e.CadenceUnit;
        entry.CadenceCount = e.CadenceCount;
    }

    public void Apply(SubscriptionPriceEntryRemoved e)
    {
        ArgumentNullException.ThrowIfNull(e);
        Entries.Remove(Entry(e.EntryId));
    }

    /// <summary>
    /// Looks the entry up without throwing: an event naming an unknown or since-removed entry is a no-op, so replay never throws
    /// and a removed entry never reappears.
    /// </summary>
    public void Apply(SubscriptionPriceEntryPaymentChanged e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var entry = Entries.FirstOrDefault(x => string.Equals(x.EntryId, e.EntryId, StringComparison.Ordinal));
        if (entry is not null)
        {
            entry.PaymentOptionId = e.PaymentOptionId;
        }
    }

    public void Apply(SubscriptionCatalogLinked e)
    {
        ArgumentNullException.ThrowIfNull(e);
        CatalogId = e.CatalogId;
    }

    public void Apply(SubscriptionCancelByChanged e)
    {
        ArgumentNullException.ThrowIfNull(e);
        CancelBy = e.CancelBy;
    }

    /// <summary>Nothing is deleted: the flag hides the subscription from reads (D-5).</summary>
    public void Apply(SubscriptionRemoved e)
    {
        ArgumentNullException.ThrowIfNull(e);
        Removed = true;
        RemovedAt = e.RecordedAt;
    }

    /// <summary>Legacy event: appends a period whose id is <c>legacy-n</c>, n being the 1-based order of the legacy pause event.</summary>
    public void Apply(SubscriptionPaused e)
    {
        ArgumentNullException.ThrowIfNull(e);
        LegacyPauseCount++;
        PausePeriods.Add(new PausePeriodState
        {
            PeriodId = LegacyPeriodIdPrefix + LegacyPauseCount.ToString(CultureInfo.InvariantCulture),
            PausedInstant = e.RecordedAt,
        });
    }

    /// <summary>Legacy event: closes the last open legacy period. With none it is a no-op, so replay never throws.</summary>
    public void Apply(SubscriptionResumed e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var open = PausePeriods.LastOrDefault(p => p.IsOpenLegacy);
        if (open is not null)
        {
            open.ResumedInstant = e.RecordedAt;
        }
        else
        {
            OrphanLegacyResumes++;
        }
    }

    /// <summary>
    /// Legacy resumes that found no open legacy period and were ignored. Internal and never serialized: it only feeds the one-line
    /// summary of <c>admin projections rebuild</c>, which aggregates the stream and reads it from the fresh instance.
    /// </summary>
    internal int OrphanLegacyResumes { get; private set; }

    public void Apply(SubscriptionPausePeriodAdded e)
    {
        ArgumentNullException.ThrowIfNull(e);
        PausePeriods.Add(new PausePeriodState { PeriodId = e.PeriodId, PauseDate = e.PauseDate, ResumeDate = e.ResumeDate });
    }

    /// <summary>The explicit dates now win, so the legacy instants are cleared. An unknown id is a no-op.</summary>
    public void Apply(SubscriptionPausePeriodChanged e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var period = PausePeriods.FirstOrDefault(p => string.Equals(p.PeriodId, e.PeriodId, StringComparison.Ordinal));
        if (period is not null)
        {
            period.PauseDate = e.PauseDate;
            period.ResumeDate = e.ResumeDate;
            period.PausedInstant = null;
            period.ResumedInstant = null;
        }
    }

    /// <summary>An unknown id is a no-op.</summary>
    public void Apply(SubscriptionPausePeriodRemoved e)
    {
        ArgumentNullException.ThrowIfNull(e);
        PausePeriods.RemoveAll(p => string.Equals(p.PeriodId, e.PeriodId, StringComparison.Ordinal));
    }

    /// <summary>
    /// The pause periods as dates, ordered by pause date then id. A date derived from a legacy instant is the household-zone date
    /// clamped to <paramref name="today"/> (D11: a date after today cannot have happened). Explicit dates are never clamped. A
    /// period with <c>resume &lt;= pause</c> is a zero-day pause: it never affected a charge and is dropped (D7).
    /// </summary>
    public IReadOnlyList<PausePeriod> ResolvePausePeriods(TimeZoneInfo zone, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var resolved = new List<PausePeriod>(PausePeriods.Count);
        foreach (var p in PausePeriods)
        {
            DateOnly? pause = p.PauseDate ?? (p.PausedInstant is { } paused ? Clamp(paused, zone, today) : null);
            if (pause is null)
            {
                continue;
            }

            DateOnly? resume = p.ResumeDate ?? (p.ResumedInstant is { } resumed ? Clamp(resumed, zone, today) : null);
            if (resume is { } r && r <= pause.Value)
            {
                continue;
            }

            resolved.Add(new PausePeriod(p.PeriodId, pause.Value, resume));
        }

        return [.. resolved.OrderBy(p => p.PauseDate).ThenBy(p => p.PeriodId, StringComparer.Ordinal)];
    }

    private static DateOnly Clamp(DateTimeOffset instant, TimeZoneInfo zone, DateOnly today)
    {
        var local = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
        return local < today ? local : today;
    }

    /// <summary>
    /// Rebuilds the state from a stream's events through <see cref="Create"/> and <c>Apply</c>, counting one version per event.
    /// Throws on an event for a subscription that does not exist and on a second <see cref="SubscriptionAdded"/>.
    /// </summary>
    public static SubscriptionState Rehydrate(IEnumerable<object> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        SubscriptionState? state = null;
        long version = 0;
        foreach (var e in events)
        {
            if (e is SubscriptionAdded added)
            {
                if (state is not null)
                {
                    throw new InvalidOperationException("A subscription stream cannot have a second added event.");
                }

                state = Create(added);
            }
            else
            {
                var target = state ?? throw new InvalidOperationException($"{e.GetType().Name} arrived before the subscription was added.");
                switch (e)
                {
                    case SubscriptionDetailsChanged x: target.Apply(x); break;
                    case SubscriptionCurrencyCorrected x: target.Apply(x); break;
                    case SubscriptionPeriodChanged x: target.Apply(x); break;
                    case SubscriptionPriceEntryAdded x: target.Apply(x); break;
                    case SubscriptionPriceEntryCorrected x: target.Apply(x); break;
                    case SubscriptionPriceEntryRemoved x: target.Apply(x); break;
                    case SubscriptionRemoved x: target.Apply(x); break;
                    case SubscriptionPaused x: target.Apply(x); break;
                    case SubscriptionResumed x: target.Apply(x); break;
                    case SubscriptionCatalogLinked x: target.Apply(x); break;
                    case SubscriptionPriceEntryPaymentChanged x: target.Apply(x); break;
                    case SubscriptionCancelByChanged x: target.Apply(x); break;
                    case SubscriptionPausePeriodAdded x: target.Apply(x); break;
                    case SubscriptionPausePeriodChanged x: target.Apply(x); break;
                    case SubscriptionPausePeriodRemoved x: target.Apply(x); break;
                    default: throw new InvalidOperationException($"Unsupported subscription event {e.GetType().Name}.");
                }
            }

            version++;
            state.Version = version;
        }

        return state ?? throw new InvalidOperationException("The stream has no events.");
    }

    private PriceEntryState Entry(string entryId) =>
        Entries.FirstOrDefault(x => string.Equals(x.EntryId, entryId, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"Unknown price entry '{entryId}'.");
}
