namespace Subscribed.Subscriptions.PaymentOptions;

/// <summary>
/// The domain state and the Marten document (<c>payment_option_view</c>) in one: a self-aggregating inline snapshot (D1), the
/// same shape as <c>SubscriptionState</c>. The <c>Apply</c> methods never touch <see cref="Version"/>. A removed option stays
/// (soft remove, D3): it is hidden from creation and pickers but keeps resolving by id.
/// </summary>
public sealed class PaymentOptionState
{
    public string Id { get; set; } = "";

    public string HouseholdId { get; set; } = "";

    public string Name { get; set; } = "";

    public string? Label { get; set; }

    /// <summary>One of <see cref="PaymentOptionBrands.Ids"/> or null. Independent of <see cref="Name"/>; survives removal.</summary>
    public string? Brand { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public bool Removed { get; set; }

    public DateTimeOffset? RemovedAt { get; set; }

    public long Version { get; set; }

    public static PaymentOptionState Create(PaymentOptionCreated e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return new PaymentOptionState
        {
            HouseholdId = e.HouseholdId,
            Name = e.Name,
            Label = e.Label,
            Brand = e.Brand,
            CreatedAt = e.RecordedAt,
        };
    }

    public void Apply(PaymentOptionChanged e)
    {
        ArgumentNullException.ThrowIfNull(e);
        Name = e.Name;
        Label = e.Label;
        Brand = e.Brand;
    }

    /// <summary>Nothing is deleted: the flag hides the option from creation and pickers (D3).</summary>
    public void Apply(PaymentOptionRemoved e)
    {
        ArgumentNullException.ThrowIfNull(e);
        Removed = true;
        RemovedAt = e.RecordedAt;
    }

    /// <summary>Rebuilds the state from a stream's events, counting one version per event.</summary>
    public static PaymentOptionState Rehydrate(IEnumerable<object> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        PaymentOptionState? state = null;
        long version = 0;
        foreach (var e in events)
        {
            if (e is PaymentOptionCreated created)
            {
                if (state is not null)
                {
                    throw new InvalidOperationException("A payment option stream cannot have a second created event.");
                }

                state = Create(created);
            }
            else
            {
                var target = state ?? throw new InvalidOperationException($"{e.GetType().Name} arrived before the payment option was created.");
                switch (e)
                {
                    case PaymentOptionChanged x: target.Apply(x); break;
                    case PaymentOptionRemoved x: target.Apply(x); break;
                    default: throw new InvalidOperationException($"Unsupported payment option event {e.GetType().Name}.");
                }
            }

            version++;
            state.Version = version;
        }

        return state ?? throw new InvalidOperationException("The stream has no events.");
    }
}
