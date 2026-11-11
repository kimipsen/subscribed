namespace Subscribed.Api.Bff;

/// <summary>Local return URL rules (design §6.1).</summary>
public static class ReturnUrl
{
    public const int MaxLength = 512;
    public const string Default = "/";

    public static bool IsValid(string? value) =>
        value is { Length: > 0 and <= MaxLength }
        && value[0] == '/'
        && !(value.Length > 1 && (value[1] == '/' || value[1] == '\\'))
        && !value.Any(c => char.IsControl(c) || c == '\\');

    /// <summary>Null/empty → default; otherwise the value when valid, else null.</summary>
    public static string? Normalize(string? value) =>
        string.IsNullOrEmpty(value) ? Default : IsValid(value) ? value : null;
}
