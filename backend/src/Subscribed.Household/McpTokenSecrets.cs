namespace Subscribed.Household;

/// <summary>
/// MCP bearer token secret scheme (plan D2). It is the calendar feed scheme on purpose: 32 random bytes as base64url without
/// padding (43 characters), only the lower-case hex SHA-256 stored, constant-time comparison. A token on the wire is exactly the
/// secret, with no prefix. It is a separate type so the two credential kinds can diverge later.
/// </summary>
public static class McpTokenSecrets
{
    public const int SecretLength = CalendarFeedSecrets.SecretLength;

    /// <summary>Length of a token id: 16 lower-case hex characters (8 random bytes).</summary>
    public const int TokenIdLength = 16;

    public static string Create() => CalendarFeedSecrets.Create();

    public static string Hash(string secret) => CalendarFeedSecrets.Hash(secret);

    public static bool IsWellFormed(string? secret) => CalendarFeedSecrets.IsWellFormed(secret);

    public static bool Matches(string? presented, string? storedHash) => CalendarFeedSecrets.Matches(presented, storedHash);

    /// <summary>A new public token id. It appears in headers, logs and the token list; the secret never does.</summary>
    public static string NewTokenId() => Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(TokenIdLength / 2));

    public static bool IsTokenId(string? value) =>
        value is { Length: TokenIdLength } && value.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
