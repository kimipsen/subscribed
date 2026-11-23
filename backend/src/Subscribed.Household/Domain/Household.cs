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
    public const string InvalidTimeZone = "invalid-time-zone";
    public const string InvalidTokenName = "invalid-token-name";
    public const string TooManyTokens = "too-many-tokens";
    public const string TokenNotFound = "token-not-found";
}

public sealed record ActiveMember(string MemberId, string Issuer, string Subject, string DisplayLabel, DateTimeOffset GrantedAt);

public sealed record ActiveMcpToken(string TokenId, string MemberId, string Name, string SecretHash, DateTimeOffset CreatedAt);

/// <summary>The Household aggregate: rehydrated from its events; decisions return new events.</summary>
public sealed class Household
{
    public const string DefaultStreamId = "household-default";
    public const int MaxNameLength = 80;
    public const int MaxLabelLength = 64;
    public const int MaxTokenNameLength = 64;
    public const int MaxActiveTokensPerMember = 20;

    private readonly Dictionary<string, ActiveMember> _members = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ActiveMcpToken> _mcpTokens = new(StringComparer.Ordinal);

    public bool Exists { get; private set; }

    public string Name { get; private set; } = "";

    public long Version { get; private set; }

    /// <summary>IANA time zone id. Existing households read as UTC.</summary>
    public string TimeZoneId { get; private set; } = TimeZoneIds.Utc;

    /// <summary>Lower-case hex SHA-256 of the active calendar feed secret, or null when no feed is active.</summary>
    public string? CalendarFeedSecretHash { get; private set; }

    public DateTimeOffset? CalendarFeedRotatedAt { get; private set; }

    public IReadOnlyCollection<ActiveMember> Members => _members.Values;

    /// <summary>Active MCP tokens (hash only). A revoked member's tokens are dropped (plan D6).</summary>
    public IReadOnlyCollection<ActiveMcpToken> McpTokens => _mcpTokens.Values;

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
                foreach (var tokenId in _mcpTokens.Values.Where(t => t.MemberId == e.MemberId).Select(t => t.TokenId).ToList())
                {
                    _mcpTokens.Remove(tokenId);
                }

                break;
            case HouseholdMemberLabelChanged e:
                if (!_members.TryGetValue(e.MemberId, out var existing))
                {
                    throw new InvalidOperationException($"Member label changed for unknown member '{e.MemberId}'; the event stream is corrupt.");
                }

                _members[e.MemberId] = existing with { DisplayLabel = e.DisplayLabel };
                break;
            case HouseholdTimeZoneChanged e:
                TimeZoneId = e.TimeZoneId;
                break;
            case HouseholdCalendarFeedRotated e:
                CalendarFeedSecretHash = e.SecretHash;
                CalendarFeedRotatedAt = e.RotatedAt;
                break;
            case HouseholdCalendarFeedRevoked:
                CalendarFeedSecretHash = null;
                CalendarFeedRotatedAt = null;
                break;
            case HouseholdMcpTokenCreated e:
                _mcpTokens[e.TokenId] = new ActiveMcpToken(e.TokenId, e.MemberId, e.Name, e.SecretHash, e.CreatedAt);
                break;
            case HouseholdMcpTokenRevoked e:
                _mcpTokens.Remove(e.TokenId);
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

    /// <summary>Changing to the current label (after trim normalization) is a no-op (no event).</summary>
    public IReadOnlyList<object> ChangeMemberLabel(string memberId, string label, DateTimeOffset now)
    {
        RequireExists();
        if (!_members.TryGetValue(memberId, out var member))
        {
            throw new HouseholdDomainException(HouseholdErrorCodes.NotAMember, "memberId", "The member is not an active member.");
        }

        var normalized = NormalizeLabel(label);
        return string.Equals(normalized, member.DisplayLabel, StringComparison.Ordinal)
            ? []
            : [new HouseholdMemberLabelChanged(memberId, normalized, now.ToUniversalTime())];
    }

    /// <summary>Changing to the current zone is a no-op (no event). An invalid id throws <c>invalid-time-zone</c>.</summary>
    public IReadOnlyList<object> ChangeTimeZone(string? id, DateTimeOffset now)
    {
        RequireExists();
        var normalized = TimeZoneIds.Normalize(id);
        return string.Equals(normalized, TimeZoneId, StringComparison.Ordinal)
            ? []
            : [new HouseholdTimeZoneChanged(normalized, now.ToUniversalTime())];
    }

    /// <summary>A rotation is an explicit act, so it always emits, even when the hash is unchanged.</summary>
    public IReadOnlyList<object> RotateCalendarFeed(string secretHash, DateTimeOffset now)
    {
        RequireExists();
        if (!IsSecretHash(secretHash))
        {
            // The service computes the hash, so a malformed one is a programming error, not user input.
            throw new ArgumentException("The calendar feed secret hash must be 64 lower-case hex characters.", nameof(secretHash));
        }

        return [new HouseholdCalendarFeedRotated(secretHash, now.ToUniversalTime())];
    }

    /// <summary>Revoking when no feed is active is a no-op (no event).</summary>
    public IReadOnlyList<object> RevokeCalendarFeed(DateTimeOffset now)
    {
        RequireExists();
        return CalendarFeedSecretHash is null ? [] : [new HouseholdCalendarFeedRevoked(now.ToUniversalTime())];
    }

    /// <summary>
    /// Creates a named MCP token for an active member: the name is trimmed to 1–64 characters without control characters
    /// (<c>invalid-token-name</c>), at most 20 active tokens per member (<c>too-many-tokens</c>). The hash is computed by the service.
    /// </summary>
    public IReadOnlyList<object> CreateMcpToken(string memberId, string? name, string secretHash, string tokenId, DateTimeOffset now)
    {
        RequireExists();
        if (!_members.ContainsKey(memberId))
        {
            throw new HouseholdDomainException(HouseholdErrorCodes.NotAMember, "memberId", "The member is not an active member.");
        }

        var normalized = NormalizeTokenName(name);
        if (_mcpTokens.Values.Count(t => t.MemberId == memberId) >= MaxActiveTokensPerMember)
        {
            throw new HouseholdDomainException(HouseholdErrorCodes.TooManyTokens, "name", $"A member can have at most {MaxActiveTokensPerMember} active tokens.");
        }

        if (!IsSecretHash(secretHash))
        {
            throw new ArgumentException("The MCP token secret hash must be 64 lower-case hex characters.", nameof(secretHash));
        }

        if (!McpTokenSecrets.IsTokenId(tokenId) || _mcpTokens.ContainsKey(tokenId))
        {
            throw new ArgumentException("The MCP token id must be 16 unused lower-case hex characters.", nameof(tokenId));
        }

        return [new HouseholdMcpTokenCreated(tokenId, memberId, normalized, secretHash, now.ToUniversalTime())];
    }

    /// <summary>A member can revoke only their own active token. Unknown, already revoked or foreign tokens throw <c>token-not-found</c>.</summary>
    public IReadOnlyList<object> RevokeMcpToken(string memberId, string tokenId, DateTimeOffset now)
    {
        RequireExists();
        if (!_mcpTokens.TryGetValue(tokenId ?? "", out var token) || !string.Equals(token.MemberId, memberId, StringComparison.Ordinal))
        {
            throw new HouseholdDomainException(HouseholdErrorCodes.TokenNotFound, "tokenId", "The token was not found.");
        }

        return [new HouseholdMcpTokenRevoked(token.TokenId, now.ToUniversalTime())];
    }

    public static string NormalizeTokenName(string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length is < 1 or > MaxTokenNameLength || trimmed.Any(char.IsControl))
        {
            throw new HouseholdDomainException(HouseholdErrorCodes.InvalidTokenName, "name", "Token name must be 1–64 characters.");
        }

        return trimmed;
    }

    public static bool IsSecretHash(string? value) =>
        value is { Length: 64 } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

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
