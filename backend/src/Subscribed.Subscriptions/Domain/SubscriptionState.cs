namespace Subscribed.Subscriptions.Domain;

public sealed class PriceEntryState
{
    public string EntryId { get; set; } = "";

    public DateOnly EffectiveFrom { get; set; }

    public decimal Amount { get; set; }

    public string CadenceUnit { get; set; } = "";

    public int CadenceCount { get; set; }

    public Cadence Cadence => new(CadenceUnit, CadenceCount);
}

/// <summary>
/// The domain state and the Marten document (<c>subscription_view</c>) in one: a self-aggregating inline snapshot (D-12). Marten
/// finds <see cref="Create"/> and the <c>Apply</c> methods by convention; <see cref="Rehydrate"/> calls the same methods, so
/// there is one evolution implementation. The <c>Apply</c> methods never touch <see cref="Version"/>.
/// </summary>
public sealed class SubscriptionState
{
    public string Id { get; set; } = "";

    public string HouseholdId { get; set; } = "";

    public string Name { get; set; } = "";

    public string? Category { get; set; }

    public string? Notes { get; set; }

    public string Currency { get; set; } = "";

    public DateOnly StartDate { get; set; }

    public DateOnly? EndDate { get; set; }

    public string InitialEntryId { get; set; } = "";

    public List<PriceEntryState> Entries { get; set; } = [];

    public DateTimeOffset AddedAt { get; set; }

    public bool Removed { get; set; }

    public DateTimeOffset? RemovedAt { get; set; }

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

    /// <summary>Nothing is deleted: the flag hides the subscription from reads (D-5).</summary>
    public void Apply(SubscriptionRemoved e)
    {
        ArgumentNullException.ThrowIfNull(e);
        Removed = true;
        RemovedAt = e.RecordedAt;
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
