using System.Security.Cryptography;
using System.Text;

namespace Subscribed.Household;

/// <summary>
/// Calendar feed secret scheme (plan D7, D8). The secret is 32 random bytes, base64url without padding (43 characters).
/// Only its lower-case hex SHA-256 is ever stored; the raw secret exists in the rotate call and its response.
/// </summary>
public static class CalendarFeedSecrets
{
    public const int SecretBytes = 32;

    /// <summary>The length of a well-formed secret: 32 bytes as base64url without padding.</summary>
    public const int SecretLength = 43;

    /// <summary>A new random secret.</summary>
    public static string Create() => Base64Url(RandomNumberGenerator.GetBytes(SecretBytes));

    /// <summary>Lower-case hex SHA-256 of the secret's UTF-8 bytes.</summary>
    public static string Hash(string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    }

    /// <summary>True for exactly 43 characters of <c>[A-Za-z0-9_-]</c>. Used to answer junk paths before any database read.</summary>
    public static bool IsWellFormed(string? secret) =>
        secret is { Length: SecretLength } && secret.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-');

    /// <summary>
    /// Constant-time comparison of the presented secret's hash with the stored hash. Returns false, never throws, for a
    /// null, malformed or differently sized stored hash or presented secret.
    /// </summary>
    public static bool Matches(string? presented, string? storedHash)
    {
        if (presented is null || !IsWellFormed(presented) || !Domain.Household.IsSecretHash(storedHash))
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(storedHash!);
        var actual = Encoding.ASCII.GetBytes(Hash(presented));
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
