using System.Reflection;
using System.Text.RegularExpressions;

namespace Subscribed.Platform.Database;

public enum GrantObjectKind
{
    Table,
    Sequence,
    Schema,
}

/// <summary>One privilege that grants.sql gives the application role.</summary>
public sealed record ExpectedGrant(GrantObjectKind Kind, string ObjectName, string Privilege);

/// <summary>Loads, renders and parses the embedded <c>Database/grants.sql</c>.</summary>
public static partial class Grants
{
    public const string SchemaToken = "__SCHEMA__";
    public const string AppRoleToken = "__APP_ROLE__";

    /// <summary>The events table: the app role must never hold UPDATE/DELETE/TRUNCATE on it.</summary>
    public const string EventsTable = "mt_events";

    /// <summary>Privileges the app role must not hold on any table in the schema.</summary>
    public static readonly IReadOnlyList<string> ForbiddenOnEveryTable = ["DELETE", "TRUNCATE", "REFERENCES", "TRIGGER"];

    /// <summary>Additional privileges the app role must not hold on the events table.</summary>
    public static readonly IReadOnlyList<string> ForbiddenOnEvents = ["UPDATE"];

    private static readonly Lazy<string> TemplateText = new(LoadTemplate);

    public static string Template => TemplateText.Value;

    [GeneratedRegex(@"^GRANT\s+(?<privs>[A-Z ,]+?)\s+ON\s+(?<kind>TABLE|SEQUENCE|SCHEMA)\s+(?<obj>[A-Za-z0-9_.]+)\s+TO\s+__APP_ROLE__;\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex GrantLine();

    public static string Render(string schema, string appRole)
    {
        DatabaseIdentifiers.Require(schema, "Database:Schema");
        DatabaseIdentifiers.Require(appRole, "Database:AppRole");
        return Template.Replace(SchemaToken, schema, StringComparison.Ordinal).Replace(AppRoleToken, appRole, StringComparison.Ordinal);
    }

    /// <summary>Executable statements of the rendered file (one per line, comments removed).</summary>
    public static IReadOnlyList<string> RenderStatements(string schema, string appRole) =>
        Render(schema, appRole)
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("--", StringComparison.Ordinal))
            .ToList();

    /// <summary>Every (object, privilege) pair granted by grants.sql, with the schema substituted.</summary>
    public static IReadOnlyList<ExpectedGrant> Expected(string schema)
    {
        DatabaseIdentifiers.Require(schema, "Database:Schema");
        var result = new List<ExpectedGrant>();
        foreach (var raw in Template.Split('\n'))
        {
            var match = GrantLine().Match(raw.Trim());
            if (!match.Success)
            {
                continue;
            }

            var kind = match.Groups["kind"].Value switch
            {
                "TABLE" => GrantObjectKind.Table,
                "SEQUENCE" => GrantObjectKind.Sequence,
                _ => GrantObjectKind.Schema,
            };
            var obj = match.Groups["obj"].Value.Replace(SchemaToken, schema, StringComparison.Ordinal);
            foreach (var privilege in match.Groups["privs"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                result.Add(new ExpectedGrant(kind, obj, privilege));
            }
        }

        return result;
    }

    /// <summary>Unqualified names of the tables and sequences that grants.sql references.</summary>
    public static IReadOnlyList<string> ExpectedRelations(string schema) =>
        Expected(schema)
            .Where(g => g.Kind != GrantObjectKind.Schema)
            .Select(g => g.ObjectName[(g.ObjectName.IndexOf('.', StringComparison.Ordinal) + 1)..])
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string LoadTemplate()
    {
        var assembly = typeof(Grants).GetTypeInfo().Assembly;
        using var stream = assembly.GetManifestResourceStream("Subscribed.Platform.Database.grants.sql")
            ?? throw new InvalidOperationException("Embedded grants.sql is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
