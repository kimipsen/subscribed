using JasperFx.Events.Projections;
using Marten;
using Marten.Events.Aggregation;
using Marten.Events.Projections;
using Subscribed.Household.Domain;
using Subscribed.Platform.EventStore;

namespace Subscribed.Household;

public sealed class HouseholdViewMember
{
    public string MemberId { get; set; } = "";

    public string Issuer { get; set; } = "";

    public string Subject { get; set; } = "";

    public string DisplayLabel { get; set; } = "";

    public DateTimeOffset GrantedAt { get; set; }
}

/// <summary>
/// Inline read model: committed in the same transaction as the events. Serves reads and the
/// per-request membership check (one primary-key read, uncached).
/// </summary>
public sealed class HouseholdView
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public long Version { get; set; }

    public List<HouseholdViewMember> Members { get; set; } = [];

    /// <summary>IANA zone id. Rows written before the time zone existed have no key and load as UTC (plan D14).</summary>
    public string TimeZoneId { get; set; } = TimeZoneIds.Utc;

    /// <summary>Lower-case hex SHA-256 of the active calendar feed secret. Never returned by any endpoint.</summary>
    public string? CalendarFeedSecretHash { get; set; }

    public DateTimeOffset? CalendarFeedRotatedAt { get; set; }

    public bool CalendarFeedEnabled => CalendarFeedSecretHash is not null;

    public HouseholdViewMember? FindMember(string issuer, string subject) =>
        Members.FirstOrDefault(m => string.Equals(m.Issuer, issuer, StringComparison.Ordinal) && string.Equals(m.Subject, subject, StringComparison.Ordinal));
}

public sealed class HouseholdViewProjection : SingleStreamProjection<HouseholdView, string>
{
    public static HouseholdView Create(HouseholdCreated e) => new() { Name = e.Name };

    public static void Apply(HouseholdRenamed e, HouseholdView view) => view.Name = e.Name;

    public static void Apply(HouseholdMemberGranted e, HouseholdView view)
    {
        view.Members.RemoveAll(m => m.MemberId == e.MemberId);
        view.Members.Add(new HouseholdViewMember
        {
            MemberId = e.MemberId,
            Issuer = e.Issuer,
            Subject = e.Subject,
            DisplayLabel = e.DisplayLabel,
            GrantedAt = e.GrantedAt,
        });
    }

    public static void Apply(HouseholdTimeZoneChanged e, HouseholdView view) => view.TimeZoneId = e.TimeZoneId;

    public static void Apply(HouseholdCalendarFeedRotated e, HouseholdView view)
    {
        view.CalendarFeedSecretHash = e.SecretHash;
        view.CalendarFeedRotatedAt = e.RotatedAt;
    }

    public static void Apply(HouseholdCalendarFeedRevoked e, HouseholdView view)
    {
        view.CalendarFeedSecretHash = null;
        view.CalendarFeedRotatedAt = null;
    }

    public static void Apply(HouseholdMemberRevoked e, HouseholdView view) => view.Members.RemoveAll(m => m.MemberId == e.MemberId);

    public static void Apply(HouseholdMemberLabelChanged e, HouseholdView view)
    {
        var member = view.Members.FirstOrDefault(m => m.MemberId == e.MemberId);
        if (member is not null)
        {
            member.DisplayLabel = e.DisplayLabel;
        }
    }
}

/// <summary>Registers the household events, aliases, view document and inline projection.</summary>
public sealed class HouseholdModule : IEventStoreModule
{
    public const string ViewAlias = "household_view";

    public void Configure(StoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (var (alias, type) in HouseholdEventTypes.ByAlias)
        {
            options.Events.MapEventType(type, alias);
        }

        options.Schema.For<HouseholdView>().Identity(x => x.Id).DocumentAlias(ViewAlias);
        options.Projections.Add<HouseholdViewProjection>(ProjectionLifecycle.Inline);
    }
}
