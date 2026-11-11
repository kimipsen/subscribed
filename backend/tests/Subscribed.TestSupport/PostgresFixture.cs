using System.Security.Cryptography;
using Npgsql;
using Subscribed.Household;
using Subscribed.Platform.Database;
using Subscribed.Platform.EventStore;
using Xunit;

namespace Subscribed.TestSupport;

/// <summary>
/// Once per test assembly (design §14.1): creates the roles subscribed_test_&lt;8hex&gt;_migrator/_app
/// and a migrated template database. Each test gets its own database copied from the template and
/// runs as the least-privilege app role with exactly grants.sql. Only the exact objects created
/// here are ever dropped; there is no sweep by prefix.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    public const string Variable = "SUBSCRIBED_TEST_DB";
    public const string Schema = "subscribed";

    private static readonly IReadOnlyList<IEventStoreModule> Modules = [new HouseholdModule()];
    private readonly SemaphoreSlim _createLock = new(1, 1);
    private readonly List<string> _databases = [];
    private int _counter;
    private bool _cleaned;

    public string RunId { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

    public string MigratorRole => $"subscribed_test_{RunId}_migrator";

    public string AppRole => $"subscribed_test_{RunId}_app";

    public string TemplateDatabase => $"subscribed_test_{RunId}_tpl";

    public string MigratorPassword { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    public string AppPassword { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    public string AdminConnectionString { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        AdminConnectionString = ResolveAdminConnectionString();
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
        {
            try
            {
                await using var probe = new NpgsqlConnection(AdminConnectionString);
                await probe.OpenAsync(timeout.Token);
            }
            catch (Exception ex) when (ex is NpgsqlException or OperationCanceledException or TimeoutException)
            {
                throw new InvalidOperationException(
                    $"PostgreSQL from {Variable} is unreachable within 30 s ({ex.GetType().Name}). Tests fail instead of skipping.", ex);
            }
        }

        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        await AdminAsync($"CREATE ROLE {MigratorRole} LOGIN PASSWORD '{MigratorPassword}'");
        await AdminAsync($"CREATE ROLE {AppRole} LOGIN PASSWORD '{AppPassword}'");
        await AdminAsync($"GRANT {MigratorRole} TO CURRENT_USER WITH SET TRUE, INHERIT TRUE");
        await AdminAsync($"CREATE DATABASE {TemplateDatabase} OWNER {MigratorRole}");
        await AdminAsync(
            $"REVOKE ALL ON DATABASE {TemplateDatabase} FROM PUBLIC; CREATE SCHEMA {Schema} AUTHORIZATION {MigratorRole}; REVOKE ALL ON SCHEMA {Schema} FROM PUBLIC",
            TemplateDatabase);
        await SchemaMigrator.MigrateAsync(ConnectionString(TemplateDatabase, MigratorRole, MigratorPassword), Schema, AppRole, Modules);
    }

    /// <summary>A fresh database copied from the migrated template (schema, functions and grants).</summary>
    public async Task<TestDatabase> CreateDatabaseAsync()
    {
        var name = $"subscribed_test_{RunId}_{Interlocked.Increment(ref _counter)}";
        await _createLock.WaitAsync();
        try
        {
            await AdminAsync($"CREATE DATABASE {name} TEMPLATE {TemplateDatabase} OWNER {MigratorRole}");
            lock (_databases)
            {
                _databases.Add(name);
            }
        }
        finally
        {
            _createLock.Release();
        }

        await AdminAsync($"REVOKE ALL ON DATABASE {name} FROM PUBLIC; GRANT CONNECT ON DATABASE {name} TO {AppRole}; GRANT CONNECT ON DATABASE {name} TO {MigratorRole}");
        return new TestDatabase(this, name);
    }

    public string ConnectionString(string database, string role, string password) =>
        new NpgsqlConnectionStringBuilder(AdminConnectionString)
        {
            Database = database,
            Username = role,
            Password = password,
            Pooling = false,
            Timeout = 15,
        }.ConnectionString;

    internal async Task DropDatabaseAsync(string name)
    {
        await AdminAsync($"DROP DATABASE IF EXISTS {name} WITH (FORCE)");
        lock (_databases)
        {
            _databases.Remove(name);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CleanupAsync();
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        _createLock.Dispose();
    }

    private async Task CleanupAsync()
    {
        if (_cleaned || string.IsNullOrEmpty(AdminConnectionString))
        {
            return;
        }

        _cleaned = true;
        List<string> remaining;
        lock (_databases)
        {
            remaining = [.. _databases];
        }

        foreach (var db in remaining)
        {
            await AdminAsync($"DROP DATABASE IF EXISTS {db} WITH (FORCE)");
        }

        await AdminAsync($"DROP DATABASE IF EXISTS {TemplateDatabase} WITH (FORCE)");
        await AdminAsync($"DROP ROLE IF EXISTS {AppRole}");
        await AdminAsync($"DROP ROLE IF EXISTS {MigratorRole}");
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        try
        {
            CleanupAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            Console.Error.WriteLine($"Test database cleanup failed ({ex.GetType().Name}).");
        }
    }

    public async Task AdminAsync(string sql, string? database = null)
    {
        var cs = database is null
            ? AdminConnectionString
            : new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = database }.ConnectionString;
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string ResolveAdminConnectionString()
    {
        var value = Environment.GetEnvironmentVariable(Variable);
        if (string.IsNullOrWhiteSpace(value))
        {
            var user = Environment.GetEnvironmentVariable("POSTGRES_USER");
            var password = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD");
            var db = Environment.GetEnvironmentVariable("POSTGRES_DB");
            if (string.IsNullOrEmpty(user) || string.IsNullOrEmpty(db))
            {
                throw new InvalidOperationException(
                    $"{Variable} is not set (and POSTGRES_USER/POSTGRES_DB are not available). Set {Variable} to an Npgsql connection string for a role with CREATEDB and CREATEROLE.");
            }

            value = $"Host=localhost;Port=5432;Username={user};Password={password};Database={db}";
        }

        return new NpgsqlConnectionStringBuilder(value) { Pooling = false }.ConnectionString;
    }
}

/// <summary>One per-test database; dropped WITH (FORCE) on dispose.</summary>
public sealed class TestDatabase(PostgresFixture fixture, string name) : IAsyncDisposable
{
    public string Name { get; } = name;

    public PostgresFixture Fixture { get; } = fixture;

    public string AppConnectionString => Fixture.ConnectionString(Name, Fixture.AppRole, Fixture.AppPassword);

    public string MigratorConnectionString => Fixture.ConnectionString(Name, Fixture.MigratorRole, Fixture.MigratorPassword);

    public async Task ExecuteAsMigratorAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(MigratorConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task ExecuteAsAppAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(AppConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<T?> ScalarAsMigratorAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(MigratorConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull or null ? default : (T)value;
    }

    public ValueTask DisposeAsync() => new(Fixture.DropDatabaseAsync(Name));
}
