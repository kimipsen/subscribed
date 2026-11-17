using System.Security.Cryptography;
using Subscribed.Household.Domain;
using Xunit;
using HouseholdAggregate = Subscribed.Household.Domain.Household;

namespace Subscribed.UnitTests;

public sealed class HouseholdCalendarFeedTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
    private static readonly string HashA = Convert.ToHexStringLower(SHA256.HashData("secret-a"u8));
    private static readonly string HashB = Convert.ToHexStringLower(SHA256.HashData("secret-b"u8));

    private static HouseholdAggregate Created(params object[] more) =>
        HouseholdAggregate.Rehydrate([new HouseholdCreated("Home", Now), .. more]);

    private static HouseholdAggregate Apply(HouseholdAggregate household, IReadOnlyList<object> events)
    {
        foreach (var e in events)
        {
            household.Apply(e);
        }

        return household;
    }

    [Fact]
    public void A_new_household_has_no_feed()
    {
        var household = Created();
        Assert.Null(household.CalendarFeedSecretHash);
        Assert.Null(household.CalendarFeedRotatedAt);
    }

    [Fact]
    public void Rotating_stores_only_the_hash_and_the_time()
    {
        var household = Created();
        var events = household.RotateCalendarFeed(HashA, Now.AddMinutes(5));

        var rotated = Assert.IsType<HouseholdCalendarFeedRotated>(Assert.Single(events));
        Assert.Equal(HashA, rotated.SecretHash);
        Assert.Equal(Now.AddMinutes(5), rotated.RotatedAt);

        var versionBefore = household.Version;
        Apply(household, events);
        Assert.Equal(HashA, household.CalendarFeedSecretHash);
        Assert.Equal(Now.AddMinutes(5), household.CalendarFeedRotatedAt);
        Assert.Equal(versionBefore + 1, household.Version);
    }

    [Fact]
    public void Rotating_again_always_emits_even_for_the_same_hash()
    {
        var household = Apply(Created(), Created().RotateCalendarFeed(HashA, Now));
        Assert.Single(household.RotateCalendarFeed(HashA, Now.AddDays(1)));

        Apply(household, household.RotateCalendarFeed(HashB, Now.AddDays(2)));
        Assert.Equal(HashB, household.CalendarFeedSecretHash);
        Assert.Equal(Now.AddDays(2), household.CalendarFeedRotatedAt);
    }

    [Fact]
    public void Revoking_clears_the_feed()
    {
        var household = Created(new HouseholdCalendarFeedRotated(HashA, Now));
        var events = household.RevokeCalendarFeed(Now.AddHours(1));

        var revoked = Assert.IsType<HouseholdCalendarFeedRevoked>(Assert.Single(events));
        Assert.Equal(Now.AddHours(1), revoked.RevokedAt);

        Apply(household, events);
        Assert.Null(household.CalendarFeedSecretHash);
        Assert.Null(household.CalendarFeedRotatedAt);
    }

    [Fact]
    public void Revoking_when_there_is_no_feed_is_a_no_op()
    {
        Assert.Empty(Created().RevokeCalendarFeed(Now));

        var household = Created(new HouseholdCalendarFeedRotated(HashA, Now), new HouseholdCalendarFeedRevoked(Now));
        Assert.Empty(household.RevokeCalendarFeed(Now.AddDays(1)));
    }

    [Fact]
    public void Revoke_then_rotate_gives_a_new_active_feed()
    {
        var household = Created(new HouseholdCalendarFeedRotated(HashA, Now), new HouseholdCalendarFeedRevoked(Now));
        Apply(household, household.RotateCalendarFeed(HashB, Now.AddDays(1)));
        Assert.Equal(HashB, household.CalendarFeedSecretHash);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08")] // upper case
    [InlineData("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a0")] // 63
    [InlineData("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a088")] // 65
    [InlineData("zf86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08")] // not hex
    public void A_malformed_hash_is_refused_and_nothing_is_emitted(string hash)
    {
        Assert.Throws<ArgumentException>(() => Created().RotateCalendarFeed(hash, Now));
    }

    [Fact]
    public void Feed_and_zone_changes_require_an_existing_household()
    {
        var household = new HouseholdAggregate();
        Assert.Equal(HouseholdErrorCodes.NotCreated, Assert.Throws<HouseholdDomainException>(() => household.RotateCalendarFeed(HashA, Now)).Code);
        Assert.Equal(HouseholdErrorCodes.NotCreated, Assert.Throws<HouseholdDomainException>(() => household.RevokeCalendarFeed(Now)).Code);
    }

    [Fact]
    public void The_new_events_hold_no_secret_email_or_token_field()
    {
        foreach (var type in new[] { typeof(HouseholdCalendarFeedRotated), typeof(HouseholdCalendarFeedRevoked), typeof(HouseholdTimeZoneChanged) })
        {
            Assert.DoesNotContain(type.GetProperties(), p => p.Name.Equals("Secret", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("Email", StringComparison.OrdinalIgnoreCase)
                || p.Name.Contains("Token", StringComparison.OrdinalIgnoreCase));
        }
    }
}
