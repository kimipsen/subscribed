using Subscribed.Platform.Identity;

namespace Subscribed.Household.Domain;

public sealed class HouseholdDomainException(string code, string field, string message) : Exception(message)
{
    public string Code { get; } = code;

    public string Field { get; } = field;
}

public static class HouseholdErrorCodes
{
    public const string InvalidName = "invalid-name";
    public const string InvalidLabel = "invalid-label";
    public const string InvalidSubject = "invalid-subject";
    public const string AlreadyCreated = "already-created";
    public const string NotCreated = "not-created";
    public const string NotAMember = "not-a-member";
    public const string LastMember = "last-member";
}

public sealed record ActiveMember(string MemberId, string Issuer, string Subject, string DisplayLabel, DateTimeOffset GrantedAt);

/// <summary>The Household aggregate: rehydrated from its events; decisions return new events.</summary>
public sealed class Household
{
    public const string DefaultStreamId = "household-default";
    public const int MaxNameLength = 80;
    public const int MaxLabelLength = 64;

    private readonly Dictionary<string, ActiveMember> _members = new(StringComparer.Ordinal);

    public bool Exists { get; private set; }

    public string Name { get; private set; } = "";

    public long Version { get; private set; }

    public IReadOnlyCollection<ActiveMember> Members => _members.Values;

    public static Household Rehydrate(IEnumerable<object> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var household = new Household();
        foreach (var e in events)
        {
            household.Apply(e);
        }

        return household;
    }

    public void Apply(object @event)
    {
        switch (@event)
        {
            case HouseholdCreated e:
                Exists = true;
                Name = e.Name;
                break;
            case HouseholdRenamed e:
                Name = e.Name;
                break;
            case HouseholdMemberGranted e:
                _members[e.MemberId] = new ActiveMember(e.MemberId, e.Issuer, e.Subject, e.DisplayLabel, e.GrantedAt);
                break;
            case HouseholdMemberRevoked e:
                _members.Remove(e.MemberId);
                break;
            default:
                throw new InvalidOperationException($"Event type {@event?.GetType().Name ?? "null"} is not a household event.");
        }

        Version++;
    }

    public IReadOnlyList<object> Create(string name, DateTimeOffset now)
    {
        if (Exists)
        {
            throw new HouseholdDomainException(HouseholdErrorCodes.AlreadyCreated, "household", "The household already exists.");
        }

        return [new HouseholdCreated(NormalizeName(name), now.ToUniversalTime())];
    }

    /// <summary>Renaming to the current name is a no-op (no event).</summary>
    public IReadOnlyList<object> Rename(string name, DateTimeOffset now)
    {
        RequireExists();
        var normalized = NormalizeName(name);
        return string.Equals(normalized, Name, StringComparison.Ordinal) ? [] : [new HouseholdRenamed(normalized, now.ToUniversalTime())];
    }

    /// <summary>Re-granting an active member is a no-op.</summary>
    public IReadOnlyList<object> Grant(string issuer, string subject, string displayLabel, DateTimeOffset now)
    {
        RequireExists();
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        if (string.IsNullOrEmpty(subject) || subject.Length > MemberIdentity.MaxSubjectLength || subject.Any(char.IsControl))
        {
            throw new HouseholdDomainException(HouseholdErrorCodes.InvalidSubject, "subject", $"Subject must be 1–{MemberIdentity.MaxSubjectLength} characters.");
        }

        var memberId = MemberIdentity.MemberId(issuer, subject);
        if (_members.ContainsKey(memberId))
        {
            return [];
        }

        return [new HouseholdMemberGranted(memberId, issuer, subject, NormalizeLabel(displayLabel), now.ToUniversalTime())];
    }

    public IReadOnlyList<object> Revoke(string memberId, DateTimeOffset now)
    {
        RequireExists();
        if (!_members.ContainsKey(memberId))
        {
            throw new HouseholdDomainException(HouseholdErrorCodes.NotAMember, "memberId", "The member is not an active member.");
        }

        if (_members.Count == 1)
        {
            throw new HouseholdDomainException(HouseholdErrorCodes.LastMember, "memberId", "The last member cannot be revoked.");
        }

        return [new HouseholdMemberRevoked(memberId, now.ToUniversalTime())];
    }

    public static string NormalizeName(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length is < 1 or > MaxNameLength || trimmed.Any(char.IsControl))
        {
            throw new HouseholdDomainException(HouseholdErrorCodes.InvalidName, "name", "Name must be 1–80 characters.");
        }

        return trimmed;
    }

    public static string NormalizeLabel(string? label)
    {
        var trimmed = (label ?? "").Trim();
        if (trimmed.Length is < 1 or > MaxLabelLength || trimmed.Any(char.IsControl))
        {
            throw new HouseholdDomainException(HouseholdErrorCodes.InvalidLabel, "displayLabel", "Label must be 1–64 characters.");
        }

        return trimmed;
    }

    private void RequireExists()
    {
        if (!Exists)
        {
            throw new HouseholdDomainException(HouseholdErrorCodes.NotCreated, "household", "The household has not been initialized.");
        }
    }
}
