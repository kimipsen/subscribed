using System.Security.Cryptography;
using System.Text;
using Subscribed.Household;
using Xunit;

namespace Subscribed.UnitTests;

public sealed class CalendarFeedSecretsTests
{
    [Fact]
    public void Create_returns_43_url_safe_characters_and_never_repeats()
    {
        var secrets = Enumerable.Range(0, 50).Select(_ => CalendarFeedSecrets.Create()).ToList();

        Assert.All(secrets, s =>
        {
            Assert.Equal(CalendarFeedSecrets.SecretLength, s.Length);
            Assert.True(CalendarFeedSecrets.IsWellFormed(s));
            Assert.DoesNotContain('=', s);
            Assert.DoesNotContain('+', s);
            Assert.DoesNotContain('/', s);
        });
        Assert.Equal(secrets.Count, secrets.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Hash_is_lower_case_hex_sha256_of_the_utf8_bytes()
    {
        var hash = CalendarFeedSecrets.Hash("abc");

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("abc"))), hash);
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash.ToLowerInvariant(), hash);
    }

    [Fact]
    public void Matches_accepts_the_secret_whose_hash_is_stored()
    {
        var secret = CalendarFeedSecrets.Create();

        Assert.True(CalendarFeedSecrets.Matches(secret, CalendarFeedSecrets.Hash(secret)));
    }

    [Fact]
    public void Matches_rejects_another_secret()
    {
        var stored = CalendarFeedSecrets.Hash(CalendarFeedSecrets.Create());

        Assert.False(CalendarFeedSecrets.Matches(CalendarFeedSecrets.Create(), stored));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("not-hex-not-hex-not-hex-not-hex-not-hex-not-hex-not-hex-not-hex!")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]
    public void Matches_returns_false_for_a_null_or_malformed_stored_hash(string? stored)
    {
        Assert.False(CalendarFeedSecrets.Matches(CalendarFeedSecrets.Create(), stored));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tooshort")]
    [InlineData("has spaces and is not base64url at all!!")]
    public void Matches_never_throws_for_a_null_or_malformed_presented_secret(string? presented)
    {
        var stored = CalendarFeedSecrets.Hash("anything");

        Assert.False(CalendarFeedSecrets.Matches(presented, stored));
    }

    [Theory]
    [InlineData(42)]
    [InlineData(44)]
    public void IsWellFormed_requires_exactly_43_characters(int length)
    {
        Assert.False(CalendarFeedSecrets.IsWellFormed(new string('a', length)));
        Assert.True(CalendarFeedSecrets.IsWellFormed(new string('a', 43)));
    }

    [Theory]
    [InlineData("=")]
    [InlineData("+")]
    [InlineData("/")]
    [InlineData(".")]
    [InlineData(" ")]
    [InlineData("é")]
    public void IsWellFormed_rejects_characters_outside_the_base64url_alphabet(string bad)
    {
        Assert.False(CalendarFeedSecrets.IsWellFormed(new string('a', 42) + bad));
    }

    [Fact]
    public void Matches_is_case_sensitive_on_the_secret()
    {
        var secret = CalendarFeedSecrets.Create();
        var swapped = new string(secret.Select(c => char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c)).ToArray());
        Assert.SkipWhen(swapped == secret, "The random secret had no letters.");

        Assert.False(CalendarFeedSecrets.Matches(swapped, CalendarFeedSecrets.Hash(secret)));
    }
}
