using JasperFx.Events.Projections;
using Marten;
using Subscribed.Platform.EventStore;

namespace Subscribed.Subscriptions.PaymentOptions;

/// <summary>
/// Registers the payment option events, the <c>payment_option_view</c> document and its inline snapshot (D1). Never use Marten's
/// delete conventions or stream archiving: the app role has no DELETE (D-5).
/// </summary>
public sealed class PaymentOptionsModule : IEventStoreModule
{
    public const string ViewAlias = "payment_option_view";

    public void Configure(StoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (var (alias, type) in PaymentOptionEventTypes.ByAlias)
        {
            options.Events.MapEventType(type, alias);
        }

        options.Schema.For<PaymentOptionState>().Identity(x => x.Id).DocumentAlias(ViewAlias);
        options.Projections.Snapshot<PaymentOptionState>(SnapshotLifecycle.Inline);
    }
}
