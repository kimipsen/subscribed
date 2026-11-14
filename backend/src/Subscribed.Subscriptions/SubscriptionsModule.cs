using JasperFx.Events.Projections;
using Marten;
using Subscribed.Platform.EventStore;
using Subscribed.Subscriptions.Domain;

namespace Subscribed.Subscriptions;

/// <summary>
/// Registers the subscription events, the <c>subscription_view</c> document and its inline snapshot (D-12): the document is
/// the self-aggregating <see cref="SubscriptionState"/>, so there is no projection class. Never use Marten's delete
/// conventions or stream archiving: the app role has no DELETE (D-5).
/// </summary>
public sealed class SubscriptionsModule : IEventStoreModule
{
    public const string ViewAlias = "subscription_view";

    public void Configure(StoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (var (alias, type) in SubscriptionEventTypes.ByAlias)
        {
            options.Events.MapEventType(type, alias);
        }

        options.Schema.For<SubscriptionState>().Identity(x => x.Id).DocumentAlias(ViewAlias);
        options.Projections.Snapshot<SubscriptionState>(SnapshotLifecycle.Inline);
    }
}
