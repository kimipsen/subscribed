using Npgsql;

namespace Subscribed.Platform.Database;

public sealed record ReadinessResult(bool Database, bool Schema, bool Privileges, IReadOnlyList<string> Problems);

/// <summary>
/// Readiness checks (design §8), executed as the application role: connectivity, schema objects and
/// version, every grants.sql privilege present, and every forbidden privilege absent.
/// </summary>
public static class ReadinessProbe
{
    public static async Task<ReadinessResult> CheckAsync(string appConnectionString, string schema, CancellationToken ct)
    {
        DatabaseIdentifiers.Require(schema, "Database:Schema");
        var problems = new List<string>();
        var builder = new NpgsqlConnectionStringBuilder(appConnectionString) { Timeout = 2, CommandTimeout = 2 };
        NpgsqlConnection connection;
        try
        {
            connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await ScalarAsync<int>(connection, "SELECT 1", ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException or OperationCanceledException)
        {
            return new ReadinessResult(false, false, false, ["database-unreachable"]);
        }

        await using (connection)
        {
            var schemaOk = await CheckSchemaAsync(connection, schema, problems, ct).ConfigureAwait(false);
            var privilegesOk = schemaOk && await CheckPrivilegesAsync(connection, schema, problems, ct).ConfigureAwait(false);
            return new ReadinessResult(true, schemaOk, privilegesOk, problems);
        }
    }

    private static async Task<bool> CheckSchemaAsync(NpgsqlConnection c, string schema, List<string> problems, CancellationToken ct)
    {
        var expected = Grants.ExpectedRelations(schema);
        await using var cmd = new NpgsqlCommand(
            "SELECT c.relname FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = @s AND c.relname = ANY(@names)", c);
        cmd.Parameters.AddWithValue("s", schema);
        cmd.Parameters.AddWithValue("names", expected.ToArray());
        var found = new HashSet<string>(StringComparer.Ordinal);
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                found.Add(reader.GetString(0));
            }
        }

        var missing = expected.Where(e => !found.Contains(e)).ToList();
        problems.AddRange(missing.Select(m => $"schema-object-missing:{m}"));
        if (missing.Count > 0)
        {
            return false;
        }

        try
        {
            var version = await ScalarAsync<int?>(c, $"SELECT max(version) FROM {schema}.{SchemaVersion.Table}", ct).ConfigureAwait(false);
            if (version != SchemaVersion.Current)
            {
                problems.Add("schema-version-mismatch");
                return false;
            }
        }
        catch (PostgresException)
        {
            problems.Add("schema-version-unreadable");
            return false;
        }

        return true;
    }

    private static async Task<bool> CheckPrivilegesAsync(NpgsqlConnection c, string schema, List<string> problems, CancellationToken ct)
    {
        foreach (var grant in Grants.Expected(schema))
        {
            var function = grant.Kind switch
            {
                GrantObjectKind.Table => "has_table_privilege",
                GrantObjectKind.Sequence => "has_sequence_privilege",
                _ => "has_schema_privilege",
            };
            await using var cmd = new NpgsqlCommand($"SELECT {function}(current_user, @o, @p)", c);
            cmd.Parameters.AddWithValue("o", grant.ObjectName);
            cmd.Parameters.AddWithValue("p", grant.Privilege);
            if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not true)
            {
                problems.Add($"privilege-missing:{grant.Privilege}:{grant.ObjectName}");
            }
        }

        // Forbidden privileges on every table of the schema, plus UPDATE on the events table.
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT c.relname, p.priv
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            CROSS JOIN unnest(@privs) AS p(priv)
            WHERE n.nspname = @s AND c.relkind IN ('r', 'p', 'v', 'm')
              AND has_table_privilege(current_user, c.oid, p.priv)
              AND (p.priv <> 'UPDATE' OR c.relname = @events)
            """, c))
        {
            cmd.Parameters.AddWithValue("s", schema);
            cmd.Parameters.AddWithValue("events", Grants.EventsTable);
            cmd.Parameters.AddWithValue("privs", Grants.ForbiddenOnEveryTable.Concat(Grants.ForbiddenOnEvents).ToArray());
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                problems.Add($"privilege-forbidden:{reader.GetString(1)}:{reader.GetString(0)}");
            }
        }

        if (await ScalarAsync<bool>(c, $"SELECT has_schema_privilege(current_user, '{schema}', 'CREATE')", ct).ConfigureAwait(false))
        {
            problems.Add("privilege-forbidden:CREATE:schema");
        }

        // Every function in the schema must be SECURITY INVOKER and owned by the schema owner.
        await using (var cmd = new NpgsqlCommand(
            """
            SELECT p.proname FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = @s AND (p.prosecdef OR p.proowner <> n.nspowner)
            """, c))
        {
            cmd.Parameters.AddWithValue("s", schema);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                problems.Add($"function-not-invoker-or-foreign-owner:{reader.GetString(0)}");
            }
        }

        return problems.Count == 0;
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection c, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, c);
        var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is DBNull or null ? default! : (T)value;
    }
}
