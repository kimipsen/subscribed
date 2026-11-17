using Subscribed.Household.Domain;
using Xunit;
using HouseholdAggregate = Subscribed.Household.Domain.Household;

namespace Subscribed.UnitTests;

public sealed class HouseholdTimeZoneTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);

    private static HouseholdAggregate Created(params object[] more) =>
        HouseholdAggregate.Rehydrate([new HouseholdCreated("Home", Now), .. more]);

    private static TimeZoneInfo Zone(string id) => TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.Zero, id, id);

    [Fact]
    public void A_new_household_defaults_to_utc()
    {
        Assert.Equal("UTC", Created().TimeZoneId);
        Assert.Equal("UTC", new HouseholdAggregate().TimeZoneId);
    }

    [Fact]
    public void Changing_the_zone_emits_one_event_and_applies_it()
    {
        var household = Created();
        var events = household.ChangeTimeZone("Europe/Copenhagen", Now.AddHours(1));

        var changed = Assert.IsType<HouseholdTimeZoneChanged>(Assert.Single(events));
        Assert.Equal("Europe/Copenhagen", changed.TimeZoneId);
        Assert.Equal(Now.AddHours(1), changed.ChangedAt);

        var versionBefore = household.Version;
        household.Apply(changed);
        Assert.Equal("Europe/Copenhagen", household.TimeZoneId);
        Assert.Equal(versionBefore + 1, household.Version);
    }

    [Fact]
    public void Changing_to_the_current_zone_is_a_no_op()
    {
        Assert.Empty(Created().ChangeTimeZone("UTC", Now));
        Assert.Empty(Created(new HouseholdTimeZoneChanged("Europe/Copenhagen", Now)).ChangeTimeZone("Europe/Copenhagen", Now));
    }

    [Fact]
    public void Changing_the_zone_before_the_household_exists_fails()
    {
        var ex = Assert.Throws<HouseholdDomainException>(() => new HouseholdAggregate().ChangeTimeZone("UTC", Now));
        Assert.Equal(HouseholdErrorCodes.NotCreated, ex.Code);
    }

    [Theory]
    [InlineData("W. Europe Standard Time")] // Windows id
    [InlineData("Romance Standard Time")]
    [InlineData("../etc/passwd")] // path-like
    [InlineData("/Europe/Copenhagen")]
    [InlineData("Europe//Copenhagen")]
    [InlineData("Europe/Copenhagen/")]
    [InlineData("Europe/../Copenhagen")]
    [InlineData("Europe\\Copenhagen")]
    [InlineData("Europe/Copenhagen ")]
    [InlineData("A/B/C/D")] // too many segments
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Nowhere/Land")] // right shape, not a zone
    [InlineData("europe/copenhagen")] // wrong case is not canonical
    public void Invalid_zone_ids_are_rejected(string? id)
    {
        var ex = Assert.Throws<HouseholdDomainException>(() => Created().ChangeTimeZone(id, Now));
        Assert.Equal(HouseholdErrorCodes.InvalidTimeZone, ex.Code);
        Assert.Equal("timeZoneId", ex.Field);
    }

    [Fact]
    public void A_zone_id_longer_than_64_characters_is_rejected()
    {
        var longId = "Etc/" + new string('a', 61);
        Assert.Equal(65, longId.Length);
        Assert.Throws<HouseholdDomainException>(() => TimeZoneIds.Normalize(longId, [Zone(longId)]));

        var maxId = "Etc/" + new string('a', 60);
        Assert.Equal(64, maxId.Length);
        Assert.Equal(maxId, TimeZoneIds.Normalize(maxId, [Zone(maxId)]));
    }

    [Fact]
    public void Utc_is_valid_even_when_the_zone_source_is_empty()
    {
        Assert.Equal("UTC", TimeZoneIds.Normalize("UTC", []));
        Assert.Equal(["UTC"], TimeZoneIds.ListFrom([]));
    }

    [Fact]
    public void A_restricted_source_accepts_only_its_zones_and_utc()
    {
        var source = new[] { Zone("Asia/Tokyo") };
        Assert.Equal("Asia/Tokyo", TimeZoneIds.Normalize("Asia/Tokyo", source));
        Assert.Equal("UTC", TimeZoneIds.Normalize("UTC", source));

        var ex = Assert.Throws<HouseholdDomainException>(() => TimeZoneIds.Normalize("Europe/Copenhagen", source));
        Assert.Equal(HouseholdErrorCodes.InvalidTimeZone, ex.Code);
    }

    [Fact]
    public void ListFrom_is_sorted_without_posix_right_or_duplicates()
    {
        var list = TimeZoneIds.ListFrom([
            Zone("Europe/Paris"),
            Zone("Asia/Tokyo"),
            Zone("posix/Europe/Paris"),
            Zone("right/Europe/Paris"),
            Zone("Europe/Paris"),
            Zone("UTC"),
            Zone("W. Europe Standard Time"),
        ]);

        Assert.Equal(["Asia/Tokyo", "Europe/Paris", "UTC"], list);
    }

    [Fact]
    public void The_system_list_is_valid_sorted_unique_and_contains_utc()
    {
        var all = TimeZoneIds.All;
        Assert.Contains("UTC", all);
        Assert.Equal(all.Order(StringComparer.Ordinal), all);
        Assert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(all, id => id.StartsWith("posix/", StringComparison.Ordinal) || id.StartsWith("right/", StringComparison.Ordinal));
        Assert.All(all, id => Assert.Equal(id, TimeZoneIds.Normalize(id)));
    }

    [Fact]
    public void TryResolve_resolves_known_ids_and_refuses_the_rest()
    {
        Assert.True(TimeZoneIds.TryResolve("UTC", out var utc));
        Assert.Equal(TimeSpan.Zero, utc.BaseUtcOffset);

        Assert.False(TimeZoneIds.TryResolve("W. Europe Standard Time", out var fallback));
        Assert.Same(TimeZoneInfo.Utc, fallback); // callers may use the out value as the UTC fallback
        Assert.False(TimeZoneIds.TryResolve(null, out _));
        Assert.False(TimeZoneIds.TryResolve("", out _));
        Assert.False(TimeZoneIds.TryResolve("../../etc/passwd", out _));
        Assert.False(TimeZoneIds.TryResolve("posix/Europe/Paris", out _));
    }

    [Fact]
    public void Europe_copenhagen_resolves_under_invariant_globalization()
    {
        // The Docker image sets invariant globalization (see Iso4217.cs). Time zone lookup does not depend on ICU, but this pins it:
        // it needs tzdata on the host. If this fails on a CI image, tzdata is missing there (plan section 8, tzdata risk).
        Assert.True(TimeZoneIds.TryResolve("Europe/Copenhagen", out var zone));
        Assert.Equal(TimeSpan.FromHours(1), zone.GetUtcOffset(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal(TimeSpan.FromHours(2), zone.GetUtcOffset(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero)));
        Assert.Contains("Europe/Copenhagen", TimeZoneIds.All);
        Assert.Equal("Europe/Copenhagen", TimeZoneIds.Normalize("Europe/Copenhagen"));
    }
}
