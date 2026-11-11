using System.Diagnostics;
using System.Net;
using System.Reflection;
using Subscribed.TestSupport;
using Xunit;

namespace Subscribed.Api.Tests;

public sealed class HealthAndStartupTests(PostgresFixture pg)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AC14_ready_is_200_with_idp_stopped_and_live_is_always_200()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        host.Stub.Available = false;
        using var ready = await host.Client.GetAsync(new Uri("/health/ready", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        var body = await ApiTestHost.JsonAsync(ready);
        Assert.Equal("Healthy", body.GetProperty("checks").GetProperty("database").GetString());
        Assert.Equal("Healthy", body.GetProperty("checks").GetProperty("privileges").GetString());
        Assert.Equal("Degraded", body.GetProperty("checks").GetProperty("identityProvider").GetString());

        using var live = await host.Client.GetAsync(new Uri("/health/live", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Fact]
    public async Task AC14_ready_is_503_for_unreachable_database()
    {
        await using var db = await pg.CreateDatabaseAsync();
        var unreachable = new Npgsql.NpgsqlConnectionStringBuilder(db.AppConnectionString) { Host = "127.0.0.1", Port = 1, Timeout = 2 }.ConnectionString;
        await using var host = await ApiTestHost.StartAsync(pg, c => c["ConnectionStrings:Subscribed"] = unreachable, db);
        using var ready = await host.Client.GetAsync(new Uri("/health/ready", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("Unhealthy", (await ApiTestHost.JsonAsync(ready)).GetProperty("checks").GetProperty("database").GetString());
        var text = await ready.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("127.0.0.1", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AC14_ready_is_503_for_unmigrated_schema_and_for_a_missing_grant()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        await host.Db.ExecuteAsMigratorAsync($"REVOKE SELECT ON subscribed.mt_doc_household_view FROM {pg.AppRole}");
        using (var missingGrant = await host.Client.GetAsync(new Uri("/health/ready", UriKind.Relative), Ct))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, missingGrant.StatusCode);
            Assert.Equal("Unhealthy", (await ApiTestHost.JsonAsync(missingGrant)).GetProperty("checks").GetProperty("privileges").GetString());
        }

        await host.Db.ExecuteAsMigratorAsync("DROP TABLE subscribed.schema_migrations");
        using var unmigrated = await host.Client.GetAsync(new Uri("/health/ready", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unmigrated.StatusCode);
        Assert.Equal("Unhealthy", (await ApiTestHost.JsonAsync(unmigrated)).GetProperty("checks").GetProperty("schema").GetString());
    }

    [Fact]
    public async Task AC13_invalid_production_config_exits_1_listing_keys_only()
    {
        var sentinel = "SENTINEL-" + Guid.NewGuid().ToString("N");
        var (exit, stdout, stderr) = await RunApiAsync(
            ["--check-config"],
            new Dictionary<string, string>
            {
                ["App__PublicOrigin"] = "http://" + sentinel + ".example", // http is not allowed in Production
                ["Oidc__Authority"] = "not a uri " + sentinel,
                ["Oidc__ClientId"] = "client",
                ["ConnectionStrings__Subscribed"] = "Host=127.0.0.1;Username=u;Password=" + sentinel,
                ["Database__Schema"] = "Bad-" + sentinel,
            });
        Assert.Equal(1, exit);
        Assert.Contains("App:PublicOrigin", stderr, StringComparison.Ordinal);
        Assert.Contains("Oidc:Authority", stderr, StringComparison.Ordinal);
        Assert.Contains("Oidc:ClientSecret", stderr, StringComparison.Ordinal);
        Assert.Contains("Database:Schema", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectionStrings:Subscribed", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(sentinel, stderr + stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AC13_invalid_config_without_check_flag_exits_1_before_starting_kestrel()
    {
        var (exit, stdout, stderr) = await RunApiAsync([], new Dictionary<string, string>());
        Assert.Equal(1, exit);
        Assert.Contains("App:PublicOrigin", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Now listening", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AC13_check_config_with_valid_config_exits_0()
    {
        var (exit, stdout, stderr) = await RunApiAsync(
            ["--check-config"],
            new Dictionary<string, string>
            {
                ["App__PublicOrigin"] = "https://app.example.invalid",
                ["Oidc__Authority"] = "https://idp.example.invalid/realms/synthetic",
                ["Oidc__ClientId"] = "subscribed",
                ["Oidc__ClientSecret"] = "synthetic-secret",
                ["ConnectionStrings__Subscribed"] = "Host=db.example.invalid;Username=subscribed_app;Password=synthetic",
            });
        Assert.True(exit == 0, stderr);
        Assert.Contains("valid", stdout, StringComparison.Ordinal);
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunApiAsync(string[] args, IDictionary<string, string> environment)
    {
        var dll = LocateApiDll();
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(dll)!,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(dll);
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        foreach (var key in psi.Environment.Keys.ToList())
        {
            if (key.StartsWith("App__", StringComparison.Ordinal) || key.StartsWith("Oidc__", StringComparison.Ordinal)
                || key.StartsWith("ConnectionStrings__", StringComparison.Ordinal) || key.StartsWith("Database__", StringComparison.Ordinal)
                || key.StartsWith("ASPNETCORE_", StringComparison.Ordinal) || key == "DOTNET_ENVIRONMENT")
            {
                psi.Environment.Remove(key);
            }
        }

        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        psi.Environment["SUBSCRIBED_SECRETS_DIR"] = Path.Combine(Path.GetDirectoryName(dll)!, "no-secrets-here");
        psi.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        foreach (var (k, v) in environment)
        {
            psi.Environment[k] = v;
        }

        using var process = Process.Start(psi)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return (process.ExitCode, await stdout, await stderr);
    }

    private static string LocateApiDll()
    {
        var configuration = typeof(HealthAndStartupTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Release";
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Subscribed.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        var dll = Path.Combine(dir.FullName, "src", "Subscribed.Api", "bin", configuration, "net11.0", "Subscribed.Api.dll");
        Assert.True(File.Exists(dll), $"Build Subscribed.Api first: {dll}");
        return dll;
    }
}
