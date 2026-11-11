using System.Security.Cryptography;
using System.Text;

namespace Subscribed.Platform.Identity;

/// <summary>Member key (issuer, subject) and its opaque id (design §5 "Identity").</summary>
public static class MemberIdentity
{
    public const int MaxSubjectLength = 255;
    public const int MaxIssuerLength = 200;

    /// <summary>The configured authority without trailing slash.</summary>
    public static string NormalizeIssuer(string authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        return authority.TrimEnd('/');
    }

    /// <summary>First 16 lowercase hex chars of SHA-256("issuer|subject").</summary>
    public static string MemberId(string issuer, string subject)
    {
        ArgumentNullException.ThrowIfNull(issuer);
        ArgumentNullException.ThrowIfNull(subject);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{issuer}|{subject}"));
        return Convert.ToHexStringLower(hash)[..16];
    }
}
