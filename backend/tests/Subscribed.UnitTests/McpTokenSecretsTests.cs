using Subscribed.Household;
using Xunit;

namespace Subscribed.UnitTests;

/// <summary>The MCP token scheme is the calendar feed scheme (plan D2): same length, alphabet, hash and constant-time match.</summary>
public sealed class McpTokenSecretsTests
{
    [Fact]
    public void Secrets_have_the_calendar_feed_shape()
    {
        var secret = McpTokenSecrets.Create();

        Assert.Equal(CalendarFeedSecrets.SecretLength, secret.Length);
        Assert.Equal(43, McpTokenSecrets.SecretLength);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", secret);
        Assert.True(McpTokenSecrets.IsWellFormed(secret));
        Assert.True(CalendarFeedSecrets.IsWellFormed(secret));
    }

    [Fact]
    public void Hash_equals_the_calendar_feed_hash_and_is_lower_case_hex()
    {
        var secret = McpTokenSecrets.Create();

        Assert.Equal(CalendarFeedSecrets.Hash(secret), McpTokenSecrets.Hash(secret));
        Assert.Matches("^[0-9a-f]{64}$", McpTokenSecrets.Hash(secret));
    }

    [Fact]
    public void Matches_accepts_the_stored_secret_only()
    {
        var secret = McpTokenSecrets.Create();
        var hash = McpTokenSecrets.Hash(secret);

        Assert.True(McpTokenSecrets.Matches(secret, hash));
        Assert.True(CalendarFeedSecrets.Matches(secret, hash));
        Assert.False(McpTokenSecrets.Matches(McpTokenSecrets.Create(), hash));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("0123456789012345678901234567890123456789012")] // 43 characters but the hash differs
    public void Matches_rejects_null_short_and_wrong_input(string? presented)
    {
        var hash = McpTokenSecrets.Hash(McpTokenSecrets.Create());

        Assert.False(McpTokenSecrets.Matches(presented, hash));
    }

    [Fact]
    public void Matches_rejects_a_null_or_malformed_stored_hash()
    {
        var secret = McpTokenSecrets.Create();

        Assert.False(McpTokenSecrets.Matches(secret, null));
        Assert.False(McpTokenSecrets.Matches(secret, "not-a-hash"));
        Assert.False(McpTokenSecrets.Matches(secret, McpTokenSecrets.Hash(secret).ToUpperInvariant()));
    }

    [Fact]
    public void Token_ids_are_16_lower_case_hex_characters_and_never_repeat()
    {
        var ids = Enumerable.Range(0, 50).Select(_ => McpTokenSecrets.NewTokenId()).ToList();

        Assert.All(ids, id =>
        {
            Assert.Matches("^[0-9a-f]{16}$", id);
            Assert.True(McpTokenSecrets.IsTokenId(id));
        });
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.False(McpTokenSecrets.IsTokenId(McpTokenSecrets.Create()));
    }
}
