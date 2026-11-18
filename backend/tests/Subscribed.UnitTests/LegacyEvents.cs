using Subscribed.Subscriptions.Domain;

namespace Subscribed.UnitTests;

/// <summary>
/// Raw legacy pause events with a chosen <c>RecordedAt</c>, appended straight into a history. A test using these cannot go
/// through the new pause decisions by accident.
/// </summary>
internal static class LegacyEvents
{
    public static SubscriptionPaused Paused(DateTimeOffset at) => new(at.ToUniversalTime());

    public static SubscriptionResumed Resumed(DateTimeOffset at) => new(at.ToUniversalTime());

    public static List<object> WithPaused(this List<object> history, DateTimeOffset at)
    {
        history.Add(Paused(at));
        return history;
    }

    public static List<object> WithResumed(this List<object> history, DateTimeOffset at)
    {
        history.Add(Resumed(at));
        return history;
    }
}
