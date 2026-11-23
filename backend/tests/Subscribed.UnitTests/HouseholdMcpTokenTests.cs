using Subscribed.Household;
using Subscribed.Household.Domain;
using Subscribed.Platform.Identity;
using Xunit;
using HouseholdAggregate = Subscribed.Household.Domain.Household;

namespace Subscribed.UnitTests;

/// <summary>Aggregate decisions and state for MCP tokens (plan WP1 steps 2 to 4).</summary>
public sealed class HouseholdMcpTokenTests
{
    private const string Issuer = "https://idp.example.invalid/realms/synthetic";
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly string MemberA = MemberIdentity.MemberId(Issuer, "subject-a");
    private static readonly string MemberB = MemberIdentity.MemberId(Issuer, "subject-b");

    private static string NewHash() => McpTokenSecrets.Hash(McpTokenSecrets.Create());

    private static HouseholdAggregate Household() => HouseholdAggregate.Rehydrate(
    [
        new HouseholdCreated("Home", Now),
        new HouseholdMemberGranted(MemberA, Issuer, "subject-a", "A", Now),
        new HouseholdMemberGranted(MemberB, Issuer, "subject-b", "B", Now),
    ]);

    private static string Create(HouseholdAggregate household, string memberId, string name = "Claude")
    {
        var tokenId = McpTokenSecrets.NewTokenId();
        foreach (var e in household.CreateMcpToken(memberId, name, NewHash(), tokenId, Now))
        {
            household.Apply(e);
        }

        return tokenId;
    }

    private static void Revoke(HouseholdAggregate household, string memberId, string tokenId)
    {
        foreach (var e in household.RevokeMcpToken(memberId, tokenId, Now))
        {
            household.Apply(e);
        }
    }

    private static HouseholdDomainException Fails(Action action) => Assert.Throws<HouseholdDomainException>(action);

    [Fact]
    public void Create_emits_one_event_that_carries_the_hash_name_and_owner()
    {
        var household = Household();
        var hash = NewHash();

        var events = household.CreateMcpToken(MemberA, "  Claude Code  ", hash, "0123456789abcdef", Now.AddMinutes(1));

        var created = Assert.IsType<HouseholdMcpTokenCreated>(Assert.Single(events));
        Assert.Equal("0123456789abcdef", created.TokenId);
        Assert.Equal(MemberA, created.MemberId);
        Assert.Equal("Claude Code", created.Name);
        Assert.Equal(hash, created.SecretHash);
        Assert.Equal(Now.AddMinutes(1), created.CreatedAt);
    }

    [Fact]
    public void Apply_tracks_the_token_and_bumps_the_version()
    {
        var household = Household();
        var before = household.Version;

        var id = Create(household, MemberA);

        var token = Assert.Single(household.McpTokens);
        Assert.Equal(id, token.TokenId);
        Assert.Equal(MemberA, token.MemberId);
        Assert.Equal(before + 1, household.Version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad\nname")]
    [InlineData("bad\tname")]
    public void Create_rejects_an_empty_or_control_character_name(string? name)
    {
        var household = Household();

        var ex = Fails(() => household.CreateMcpToken(MemberA, name, NewHash(), McpTokenSecrets.NewTokenId(), Now));

        Assert.Equal(HouseholdErrorCodes.InvalidTokenName, ex.Code);
        Assert.Equal("name", ex.Field);
    }

    [Fact]
    public void Create_accepts_64_characters_and_rejects_65_after_trim()
    {
        var household = Household();

        Assert.Single(household.CreateMcpToken(MemberA, " " + new string('n', 64) + " ", NewHash(), McpTokenSecrets.NewTokenId(), Now));
        var ex = Fails(() => household.CreateMcpToken(MemberA, new string('n', 65), NewHash(), McpTokenSecrets.NewTokenId(), Now));
        Assert.Equal(HouseholdErrorCodes.InvalidTokenName, ex.Code);
    }

    [Fact]
    public void Create_requires_an_active_member()
    {
        var household = Household();

        var ex = Fails(() => household.CreateMcpToken("ffffffffffffffff", "x", NewHash(), McpTokenSecrets.NewTokenId(), Now));

        Assert.Equal(HouseholdErrorCodes.NotAMember, ex.Code);
    }

    [Fact]
    public void Create_requires_a_well_formed_hash_and_token_id()
    {
        var household = Household();

        Assert.Throws<ArgumentException>(() => household.CreateMcpToken(MemberA, "x", "not-a-hash", McpTokenSecrets.NewTokenId(), Now));
        Assert.Throws<ArgumentException>(() => household.CreateMcpToken(MemberA, "x", NewHash(), "short", Now));
        var id = Create(household, MemberA);
        Assert.Throws<ArgumentException>(() => household.CreateMcpToken(MemberA, "x", NewHash(), id, Now));
    }

    [Fact]
    public void A_member_can_hold_20_active_tokens_and_the_21st_is_too_many_tokens()
    {
        var household = Household();
        for (var i = 0; i < HouseholdAggregate.MaxActiveTokensPerMember; i++)
        {
            Create(household, MemberA, "t" + i);
        }

        var ex = Fails(() => household.CreateMcpToken(MemberA, "one more", NewHash(), McpTokenSecrets.NewTokenId(), Now));

        Assert.Equal(HouseholdErrorCodes.TooManyTokens, ex.Code);
        Assert.Equal(20, household.McpTokens.Count);
    }

    [Fact]
    public void The_cap_is_per_member_and_a_revoked_token_frees_a_slot()
    {
        var household = Household();
        var ids = Enumerable.Range(0, 20).Select(i => Create(household, MemberA, "t" + i)).ToList();

        Create(household, MemberB);
        Revoke(household, MemberA, ids[0]);
        Create(household, MemberA, "again");

        Assert.Equal(20, household.McpTokens.Count(t => t.MemberId == MemberA));
        Assert.Single(household.McpTokens, t => t.MemberId == MemberB);
    }

    [Fact]
    public void Revoke_removes_only_that_token_and_two_tokens_revoke_independently()
    {
        var household = Household();
        var first = Create(household, MemberA, "first");
        var second = Create(household, MemberA, "second");

        var events = household.RevokeMcpToken(MemberA, first, Now.AddHours(1));

        var revoked = Assert.IsType<HouseholdMcpTokenRevoked>(Assert.Single(events));
        Assert.Equal(first, revoked.TokenId);
        Assert.Equal(Now.AddHours(1), revoked.RevokedAt);
        household.Apply(revoked);
        Assert.Equal(second, Assert.Single(household.McpTokens).TokenId);
    }

    [Fact]
    public void Revoking_an_unknown_already_revoked_or_foreign_token_is_token_not_found()
    {
        var household = Household();
        var own = Create(household, MemberA);
        var foreign = Create(household, MemberB);

        Assert.Equal(HouseholdErrorCodes.TokenNotFound, Fails(() => household.RevokeMcpToken(MemberA, "0000000000000000", Now)).Code);
        Assert.Equal(HouseholdErrorCodes.TokenNotFound, Fails(() => household.RevokeMcpToken(MemberA, foreign, Now)).Code);
        Revoke(household, MemberA, own);
        Assert.Equal(HouseholdErrorCodes.TokenNotFound, Fails(() => household.RevokeMcpToken(MemberA, own, Now)).Code);
        Assert.Single(household.McpTokens, t => t.TokenId == foreign);
    }

    [Fact]
    public void Revoking_a_member_drops_that_members_tokens_and_a_re_grant_has_none()
    {
        var household = Household();
        Create(household, MemberA, "a1");
        Create(household, MemberA, "a2");
        var keep = Create(household, MemberB);

        foreach (var e in household.Revoke(MemberA, Now))
        {
            household.Apply(e);
        }

        Assert.Equal(keep, Assert.Single(household.McpTokens).TokenId);
        foreach (var e in household.Grant(Issuer, "subject-a", "A again", Now.AddDays(1)))
        {
            household.Apply(e);
        }

        Assert.DoesNotContain(household.McpTokens, t => t.MemberId == MemberA);
    }

    [Fact]
    public void Events_never_carry_a_raw_secret()
    {
        var household = Household();
        var secret = McpTokenSecrets.Create();

        var events = household.CreateMcpToken(MemberA, "x", McpTokenSecrets.Hash(secret), McpTokenSecrets.NewTokenId(), Now);

        Assert.DoesNotContain(secret, HouseholdEventTypes.Serialize(events[0]), StringComparison.Ordinal);
        Assert.Contains(McpTokenSecrets.Hash(secret), HouseholdEventTypes.Serialize(events[0]), StringComparison.Ordinal);
    }
}
