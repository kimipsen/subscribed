using System.Text.Json;
using Subscribed.Household.Domain;
using Subscribed.Platform.Identity;
using Xunit;
using HouseholdAggregate = Subscribed.Household.Domain.Household;

namespace Subscribed.UnitTests;

public sealed class DomainTests
{
    private const string Issuer = "https://idp.example.invalid/realms/synthetic";
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);

    private static HouseholdAggregate Created(params object[] more) =>
        HouseholdAggregate.Rehydrate([new HouseholdCreated("Home", Now), .. more]);

    [Theory]
    [InlineData("  Home  ", "Home")]
    [InlineData("A", "A")]
    [InlineData("Ærø Hjem 😀", "Ærø Hjem 😀")]
    public void AC7_name_is_trimmed(string input, string expected) => Assert.Equal(expected, HouseholdAggregate.NormalizeName(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("bad\u0007name")]
    [InlineData("line\nbreak")]
    public void AC7_invalid_names_are_rejected(string? input)
    {
        var ex = Assert.Throws<HouseholdDomainException>(() => HouseholdAggregate.NormalizeName(input));
        Assert.Equal(HouseholdErrorCodes.InvalidName, ex.Code);
        Assert.Equal("name", ex.Field);
    }

    [Fact]
    public void AC7_name_length_limit_is_80()
    {
        Assert.Equal(80, HouseholdAggregate.NormalizeName(new string('n', 80)).Length);
        Assert.Throws<HouseholdDomainException>(() => HouseholdAggregate.NormalizeName(new string('n', 81)));
    }

    [Fact]
    public void AC7_household_is_created_once()
    {
        var events = new HouseholdAggregate().Create("Home", Now);
        var created = HouseholdAggregate.Rehydrate(events);
        Assert.True(created.Exists);
        Assert.Equal(1, created.Version);
        var ex = Assert.Throws<HouseholdDomainException>(() => created.Create("Again", Now));
        Assert.Equal(HouseholdErrorCodes.AlreadyCreated, ex.Code);
    }

    [Fact]
    public void AC7_commands_on_missing_household_fail()
    {
        var empty = new HouseholdAggregate();
        Assert.Throws<HouseholdDomainException>(() => empty.Rename("x", Now));
        Assert.Throws<HouseholdDomainException>(() => empty.Grant(Issuer, "s", "L", Now));
    }

    [Fact]
    public void AC7_regranting_an_active_member_is_a_noop()
    {
        var household = Created();
        var granted = household.Grant(Issuer, "subject-a", "Member A", Now);
        Assert.Single(granted);
        household.Apply(granted[0]);
        Assert.Empty(household.Grant(Issuer, "subject-a", "Member A again", Now));
    }

    [Fact]
    public void AC7_revoking_a_non_member_or_the_last_member_fails()
    {
        var a = MemberIdentity.MemberId(Issuer, "subject-a");
        var household = Created(new HouseholdMemberGranted(a, Issuer, "subject-a", "A", Now));

        Assert.Equal(HouseholdErrorCodes.NotAMember, Assert.Throws<HouseholdDomainException>(() => household.Revoke("0000000000000000", Now)).Code);
        Assert.Equal(HouseholdErrorCodes.LastMember, Assert.Throws<HouseholdDomainException>(() => household.Revoke(a, Now)).Code);

        var b = MemberIdentity.MemberId(Issuer, "subject-b");
        household.Apply(new HouseholdMemberGranted(b, Issuer, "subject-b", "B", Now));
        var revoked = household.Revoke(a, Now);
        Assert.Equal(new HouseholdMemberRevoked(a, Now), Assert.Single(revoked));
    }

    [Fact]
    public void Rename_to_the_same_name_is_a_noop_and_versions_count_events()
    {
        var household = Created(new HouseholdRenamed("Second", Now));
        Assert.Equal(2, household.Version);
        Assert.Empty(household.Rename("  Second ", Now));
        Assert.Single(household.Rename("Third", Now));
    }

    [Fact]
    public void Subjects_and_labels_are_bounded()
    {
        var household = Created();
        Assert.Throws<HouseholdDomainException>(() => household.Grant(Issuer, new string('s', 256), "L", Now));
        Assert.Throws<HouseholdDomainException>(() => household.Grant(Issuer, "", "L", Now));
        Assert.Throws<HouseholdDomainException>(() => household.Grant(Issuer, "s", new string('l', 65), Now));
        Assert.Single(household.Grant(Issuer, new string('s', 255), new string('l', 64), Now));
    }

    [Fact]
    public void Member_id_is_first_16_hex_of_sha256_issuer_pipe_subject()
    {
        // printf 'https://idp.example.invalid/realms/synthetic|subject-a' | sha256sum
        var id = MemberIdentity.MemberId(Issuer, "subject-a");
        Assert.Matches("^[0-9a-f]{16}$", id);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("https://idp.example.invalid/realms/synthetic|subject-a"u8))[..16], id);
        Assert.NotEqual(id, MemberIdentity.MemberId(Issuer + "/", "subject-a"));
        Assert.Equal(Issuer, MemberIdentity.NormalizeIssuer(Issuer + "/"));
    }

    [Theory]
    [InlineData(HouseholdEventTypes.Created, typeof(HouseholdCreated))]
    [InlineData(HouseholdEventTypes.Renamed, typeof(HouseholdRenamed))]
    [InlineData(HouseholdEventTypes.MemberGranted, typeof(HouseholdMemberGranted))]
    [InlineData(HouseholdEventTypes.MemberRevoked, typeof(HouseholdMemberRevoked))]
    public void AC6_golden_fixtures_deserialize_and_round_trip(string alias, Type type)
    {
        var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "events", alias + ".json")).Trim();
        var e = HouseholdEventTypes.Deserialize(alias, json);
        Assert.IsType(type, e);
        Assert.Equal(alias, HouseholdEventTypes.AliasOf(type));

        // Every field of the fixture is populated (catches renamed properties).
        foreach (var property in JsonElement.Parse(json).EnumerateObject())
        {
            var clr = type.GetProperties().Single(p => string.Equals(p.Name, property.Name, StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(clr.GetValue(e));
        }

        Assert.Equal(e, HouseholdEventTypes.Deserialize(alias, HouseholdEventTypes.Serialize(e)));
        Assert.Contains("\"" + char.ToLowerInvariant(type.GetProperties()[0].Name[0]), HouseholdEventTypes.Serialize(e), StringComparison.Ordinal);
    }

    [Fact]
    public void AC6_unknown_alias_fails_loudly()
    {
        var ex = Assert.Throws<UnknownEventAliasException>(() => HouseholdEventTypes.Deserialize("household.bogus.v1", "{}"));
        Assert.Equal("household.bogus.v1", ex.Alias);
        Assert.Throws<UnknownEventAliasException>(() => HouseholdEventTypes.Resolve("household.created.v2"));
    }

    [Fact]
    public void AC6_golden_fixture_set_matches_registered_aliases()
    {
        var files = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "events"), "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f)).Order().ToList();
        Assert.Equal(HouseholdEventTypes.ByAlias.Keys.Concat(Subscribed.Subscriptions.Domain.SubscriptionEventTypes.ByAlias.Keys).Order().ToList(), files);
    }

    [Fact]
    public void Events_carry_no_email_or_token_fields()
    {
        foreach (var type in HouseholdEventTypes.ByAlias.Values)
        {
            Assert.DoesNotContain(type.GetProperties(), p => p.Name.Contains("Email", StringComparison.OrdinalIgnoreCase) || p.Name.Contains("Token", StringComparison.OrdinalIgnoreCase));
        }
    }
}
