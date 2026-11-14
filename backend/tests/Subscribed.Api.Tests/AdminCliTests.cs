using Microsoft.Extensions.Configuration;
using Subscribed.Household;
using Subscribed.Api.Admin;
using Subscribed.Platform.EventStore;
using Subscribed.Subscriptions;
using Subscribed.Subscriptions.Domain;
using Subscribed.TestSupport;
using Xunit;

namespace Subscribed.Api.Tests;

public sealed class AdminCliTests(PostgresFixture pg)
{
    private static async Task<(int Exit, string Out, string Err)> RunAsync(Dictionary<string, string?> config, params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await AdminCli.RunAsync(args, new ConfigurationBuilder().AddInMemoryCollection(config).Build(), false, stdout, stderr, TimeProvider.System);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private Dictionary<string, string?> Config(TestDatabase db) => new()
    {
        ["ConnectionStrings:Subscribed"] = db.AppConnectionString,
        ["ConnectionStrings:SubscribedMigrator"] = db.MigratorConnectionString,
        ["Database:AppRole"] = pg.AppRole,
        ["Oidc:Authority"] = "https://idp.example.invalid/realms/synthetic/",
    };

    [Fact]
    public async Task Init_grant_show_revoke_and_exit_codes()
    {
        await using var db = await pg.CreateDatabaseAsync();
        var config = Config(db);

        var init = await RunAsync(config, "household", "init", "--name", "Synthetic Household", "--owner-subject", "subject-a", "--owner-label", "Owner");
        Assert.True(init.Exit == AdminCli.Ok, init.Err);
        Assert.Equal(AdminCli.Conflict, (await RunAsync(config, "household", "init", "--name", "Again", "--owner-subject", "subject-b", "--owner-label", "B")).Exit);

        Assert.Equal(AdminCli.Ok, (await RunAsync(config, "household", "grant", "--subject", "subject-b", "--label", "Member B")).Exit);
        var show = await RunAsync(config, "household", "show");
        Assert.Contains("version 3", show.Out, StringComparison.Ordinal);
        Assert.Contains("subject subject-b", show.Out, StringComparison.Ordinal);

        // The issuer is the configured authority without the trailing slash.
        var memberId = Platform.Identity.MemberIdentity.MemberId("https://idp.example.invalid/realms/synthetic", "subject-a");
        Assert.Contains(memberId, show.Out, StringComparison.Ordinal);
        Assert.Equal(AdminCli.Ok, (await RunAsync(config, "household", "revoke", "--member-id", memberId)).Exit);
        Assert.Equal(AdminCli.UsageError, (await RunAsync(config, "household", "revoke", "--member-id", "0000000000000000")).Exit);

        Assert.Equal(AdminCli.UsageError, (await RunAsync(config, "household", "dance")).Exit);
        Assert.Equal(AdminCli.UsageError, (await RunAsync(config, "household", "grant", "--subject")).Exit);
    }

    [Fact]
    public async Task Missing_configuration_lists_keys_only_and_exits_1()
    {
        var (exit, stdout, stderr) = await RunAsync(new Dictionary<string, string?> { ["Database:AppRole"] = "Bad-Role-SENTINEL" }, "db", "migrate");
        Assert.Equal(AdminCli.InvalidConfiguration, exit);
        Assert.Contains("ConnectionStrings:SubscribedMigrator", stderr, StringComparison.Ordinal);
        Assert.Contains("Database:AppRole", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("SENTINEL", stderr + stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Migrate_is_idempotent_rebuild_restores_view_and_patch_writes_a_file()
    {
        await using var db = await pg.CreateDatabaseAsync();
        var config = Config(db);
        Assert.Equal(AdminCli.Ok, (await RunAsync(config, "household", "init", "--name", "Home", "--owner-subject", "subject-a", "--owner-label", "Owner")).Exit);
        var before = await db.ScalarAsMigratorAsync<string>("SELECT data::text FROM subscribed.mt_doc_household_view");

        var migrate = await RunAsync(config, "db", "migrate");
        Assert.True(migrate.Exit == AdminCli.Ok, migrate.Err);

        await db.ExecuteAsMigratorAsync("DELETE FROM subscribed.mt_doc_household_view");
        var rebuild = await RunAsync(config, "projections", "rebuild");
        Assert.True(rebuild.Exit == AdminCli.Ok, rebuild.Err);
        Assert.Equal(before, await db.ScalarAsMigratorAsync<string>("SELECT data::text FROM subscribed.mt_doc_household_view"));

        var file = Path.Combine(AppContext.BaseDirectory, $"patch-{db.Name}.sql");
        try
        {
            var patch = await RunAsync(config, "db", "patch", "--output", file);
            Assert.True(patch.Exit == AdminCli.Ok, patch.Err);
            Assert.True(File.Exists(file));
        }
        finally
        {
            File.Delete(file);
        }

        // The app role cannot run the migrator commands.
        var asApp = new Dictionary<string, string?>(config) { ["ConnectionStrings:SubscribedMigrator"] = db.AppConnectionString };
        Assert.Equal(AdminCli.DatabaseError, (await RunAsync(asApp, "projections", "rebuild")).Exit);
    }

    [Fact]
    public async Task Rebuild_restores_active_and_removed_subscription_documents_byte_identical_and_reports_both_counts()
    {
        await using var db = await pg.CreateDatabaseAsync();
        var config = Config(db);
        Assert.Equal(AdminCli.Ok, (await RunAsync(config, "household", "init", "--name", "Home", "--owner-subject", "subject-a", "--owner-label", "Owner")).Exit);

        await using (var store = SubscribedStore.Create(db.AppConnectionString, PostgresFixture.Schema, StoreRole.App, SubscribedApp.Modules))
        {
            var service = new SubscriptionService(store, TimeProvider.System);
            var context = new WriteContext(WriteContext.AdminCli, null);
            NewSubscription Sub(string name) => SubscriptionDecisions.ParseAdd(new AddInput(name, "Streaming", "Notes", "DKK", "2024-03-01", null, "129.00", "month", 1));
            var active = await service.AddAsync(Sub("Active"), context, TestContext.Current.CancellationToken);
            await service.AddPriceEntryAsync(SubscriptionIds.Bare(active.Id), active.Version, SubscriptionDecisions.ParsePriceEntry(new PriceInput("2025-01-01", "139.00", "month", 1)), context, TestContext.Current.CancellationToken);
            var removed = await service.AddAsync(Sub("Removed"), context, TestContext.Current.CancellationToken);
            await service.RemoveAsync(SubscriptionIds.Bare(removed.Id), removed.Version, context, TestContext.Current.CancellationToken);
        }

        const string sql = "SELECT string_agg(id || ':' || data::text, '|' ORDER BY id) FROM subscribed.mt_doc_subscription_view";
        var before = await db.ScalarAsMigratorAsync<string>(sql);
        Assert.NotNull(before);
        await db.ExecuteAsMigratorAsync("DELETE FROM subscribed.mt_doc_subscription_view");
        Assert.Null(await db.ScalarAsMigratorAsync<string>(sql));

        var rebuild = await RunAsync(config, "projections", "rebuild");

        Assert.True(rebuild.Exit == AdminCli.Ok, rebuild.Err);
        Assert.Equal(before, await db.ScalarAsMigratorAsync<string>(sql));
        Assert.Contains("Rebuilt 1 household view document(s).", rebuild.Out, StringComparison.Ordinal);
        Assert.Contains("Rebuilt 2 subscription view document(s).", rebuild.Out, StringComparison.Ordinal);
    }
}
