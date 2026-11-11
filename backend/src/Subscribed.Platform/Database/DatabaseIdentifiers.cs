using System.Text.RegularExpressions;

namespace Subscribed.Platform.Database;

/// <summary>Validation for schema and role names that are interpolated into DDL/DCL.</summary>
public static partial class DatabaseIdentifiers
{
    public const string DefaultSchema = "subscribed";

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    public static bool IsValid(string? value) => value is not null && IdentifierPattern().IsMatch(value);

    public static string Require(string? value, string configurationKey)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException($"Configuration key '{configurationKey}' is not a valid identifier.", configurationKey);
        }

        return value!;
    }
}
