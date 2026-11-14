using Marten;
using Npgsql;
using Subscribed.Platform.EventStore;
using Weasel.Core;
using Weasel.Core.Migrations;

namespace Subscribed.Platform.Database;

/// <summary>
/// Schema versioning. Marten computes the DDL for the configured store (diff-based, never drops);
/// <see cref="Current"/> is bumped whenever the store configuration changes the schema. Readiness
/// requires <c>max(version) = Current</c>, so an unmigrated database never serves traffic.
/// </summary>
public static class SchemaVersion
{
    public const int Current = 2;

    public const string Description = "subscriptions: subscription view document";

    public const string Table = "schema_migrations";
}

public static class SchemaMigrator
{
    /// <summary>
    /// Runs as the migrator (schema owner): creates/updates Marten objects, records the schema
    /// version, then re-applies grants.sql in one transaction.
    /// </summary>
    public static async Task MigrateAsync(string migratorConnectionString, string schema, string appRole, IEnumerable<IEventStoreModule> modules, CancellationToken ct = default)
    {
        DatabaseIdentifiers.Require(schema, "Database:Schema");
        DatabaseIdentifiers.Require(appRole, "Database:AppRole");

        await using (var store = SubscribedStore.Create(migratorConnectionString, schema, StoreRole.Migrator, modules))
        {
            await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync().ConfigureAwait(false);
        }

        await using var connection = new NpgsqlConnection(migratorConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        await ExecuteAsync(connection, tx,
            $"CREATE TABLE IF NOT EXISTS {schema}.{SchemaVersion.Table} (version integer PRIMARY KEY, description text NOT NULL, applied_at timestamptz NOT NULL DEFAULT now())", ct).ConfigureAwait(false);
        await using (var insert = new NpgsqlCommand(
            $"INSERT INTO {schema}.{SchemaVersion.Table} (version, description) VALUES (@v, @d) ON CONFLICT (version) DO NOTHING", connection, tx))
        {
            insert.Parameters.AddWithValue("v", SchemaVersion.Current);
            insert.Parameters.AddWithValue("d", SchemaVersion.Description);
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var statement in Grants.RenderStatements(schema, appRole))
        {
            await ExecuteAsync(connection, tx, statement, ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Writes the DDL Marten would apply (for DBA review). Nothing is executed.</summary>
    public static async Task WritePatchAsync(string migratorConnectionString, string schema, IEnumerable<IEventStoreModule> modules, string outputFile)
    {
        await using var store = SubscribedStore.Create(migratorConnectionString, schema, StoreRole.Migrator, modules);
        await store.Storage.Database.WriteMigrationFileAsync(outputFile).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction tx, string sql, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection, tx);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
