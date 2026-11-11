using Subscribed.Api.Configuration;
using Subscribed.Household;
using Subscribed.Household.Domain;
using Subscribed.Platform.Database;
using Subscribed.Platform.EventStore;

namespace Subscribed.Api.Admin;

/// <summary>
/// Operator commands. There is no self-registration: membership is only granted here.
/// Exit codes: 0 ok, 1 invalid configuration, 2 usage/domain error, 3 conflict, 4 database error.
/// </summary>
public static class AdminCli
{
    public const int Ok = 0;
    public const int InvalidConfiguration = 1;
    public const int UsageError = 2;
    public const int Conflict = 3;
    public const int DatabaseError = 4;

    public const string Usage =
        """
        Usage: Subscribed.Api admin <command>
          db migrate                                   apply schema + grants.sql (migrator)
          db patch --output <file>                     write pending DDL for DBA review (migrator)
          projections rebuild                          delete and reproject views from events (migrator)
          household init --name <n> --owner-subject <sub> --owner-label <label>
          household grant --subject <sub> --label <label>
          household revoke --member-id <id>
          household show
        """;

    public static Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var (configuration, isDevelopment) = ConfigurationSources.BuildForCli();
        return RunAsync(args, configuration, isDevelopment, stdout, stderr, TimeProvider.System);
    }

    public static async Task<int> RunAsync(string[] args, IConfiguration configuration, bool isDevelopment, TextWriter stdout, TextWriter stderr, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        var command = string.Join(' ', args.TakeWhile(a => !a.StartsWith("--", StringComparison.Ordinal)));
        var options = ParseOptions(args);
        var scope = command switch
        {
            "db migrate" => ConfigScope.MigratorDatabase | ConfigScope.AppRole,
            "db patch" or "projections rebuild" => ConfigScope.MigratorDatabase,
            "household init" or "household grant" => ConfigScope.AppDatabase | ConfigScope.Authority,
            "household revoke" or "household show" => ConfigScope.AppDatabase,
            _ => (ConfigScope?)null,
        };
        if (scope is null || options is null)
        {
            await stderr.WriteLineAsync(Usage);
            return UsageError;
        }

        var (settings, invalid) = SettingsValidator.Validate(configuration, isDevelopment, scope.Value);
        if (settings is null)
        {
            await stderr.WriteLineAsync("Invalid configuration keys: " + string.Join(", ", invalid));
            return InvalidConfiguration;
        }

        try
        {
            return command switch
            {
                "db migrate" => await MigrateAsync(settings, stdout),
                "db patch" => await PatchAsync(settings, options, stdout, stderr),
                "projections rebuild" => await RebuildAsync(settings, stdout),
                _ => await HouseholdAsync(command, settings, options, stdout, stderr, time),
            };
        }
        catch (HouseholdDomainException ex)
        {
            await stderr.WriteLineAsync($"Rejected: {ex.Code}: {ex.Message}");
            return ex.Code == HouseholdErrorCodes.AlreadyCreated ? Conflict : UsageError;
        }
        catch (Exception ex) when (ex is EventStoreException || EventStoreErrors.Map(ex) is not null)
        {
            var failure = ex is EventStoreException e ? e.Failure : EventStoreErrors.Map(ex)!;
            await stderr.WriteLineAsync($"Failed: {failure.Kind}{(failure.ObjectName is null ? "" : " " + failure.ObjectName)}");
            return failure.Kind == EventStoreFailureKind.ConcurrencyConflict ? Conflict : DatabaseError;
        }
    }

    private static async Task<int> MigrateAsync(SubscribedSettings settings, TextWriter stdout)
    {
        await SchemaMigrator.MigrateAsync(settings.MigratorConnectionString!, settings.Schema, settings.AppRole!, SubscribedApp.Modules);
        await stdout.WriteLineAsync($"Schema '{settings.Schema}' migrated to version {SchemaVersion.Current}; grants applied to '{settings.AppRole}'.");
        return Ok;
    }

    private static async Task<int> PatchAsync(SubscribedSettings settings, Dictionary<string, string> options, TextWriter stdout, TextWriter stderr)
    {
        if (!options.TryGetValue("output", out var output) || string.IsNullOrWhiteSpace(output))
        {
            await stderr.WriteLineAsync(Usage);
            return UsageError;
        }

        await SchemaMigrator.WritePatchAsync(settings.MigratorConnectionString!, settings.Schema, SubscribedApp.Modules, output);
        await stdout.WriteLineAsync($"Pending DDL written to {output}. Nothing was executed.");
        return Ok;
    }

    private static async Task<int> RebuildAsync(SubscribedSettings settings, TextWriter stdout)
    {
        await using var store = SubscribedStore.Create(settings.MigratorConnectionString!, settings.Schema, StoreRole.Migrator, SubscribedApp.Modules);
        var count = await HouseholdProjectionRebuilder.RebuildAsync(store);
        await stdout.WriteLineAsync($"Rebuilt {count} household view document(s).");
        return Ok;
    }

    private static async Task<int> HouseholdAsync(string command, SubscribedSettings settings, Dictionary<string, string> options, TextWriter stdout, TextWriter stderr, TimeProvider time)
    {
        string? Opt(string key) => options.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
        await using var store = SubscribedStore.Create(settings.AppConnectionString, settings.Schema, StoreRole.App, SubscribedApp.Modules);
        var service = new HouseholdService(store, time);
        var context = new WriteContext(WriteContext.AdminCli, null);

        switch (command)
        {
            case "household init" when Opt("name") is { } name && Opt("owner-subject") is { } subject && Opt("owner-label") is { } label:
                var created = await service.InitializeAsync(name, settings.Issuer, subject, label, context);
                await stdout.WriteLineAsync($"Household initialized (version {created.Version}); owner member id {created.Members[0].MemberId}.");
                return Ok;
            case "household grant" when Opt("subject") is { } subject && Opt("label") is { } label:
                var granted = await RetryOnceAsync(() => service.GrantAsync(settings.Issuer, subject, label, context));
                await stdout.WriteLineAsync($"Granted. Version {granted.Version}; member id {granted.FindMember(settings.Issuer, subject)?.MemberId}.");
                return Ok;
            case "household revoke" when Opt("member-id") is { } memberId:
                var revoked = await RetryOnceAsync(() => service.RevokeAsync(memberId, context));
                await stdout.WriteLineAsync($"Revoked. Version {revoked.Version}.");
                return Ok;
            case "household show":
                var view = await service.GetViewAsync();
                if (view is null)
                {
                    await stdout.WriteLineAsync("Household not initialized.");
                    return Ok;
                }

                await stdout.WriteLineAsync($"{view.Id} version {view.Version}: {view.Name}");
                foreach (var m in view.Members)
                {
                    await stdout.WriteLineAsync($"  {m.MemberId}  {m.DisplayLabel}  granted {m.GrantedAt:O}  subject {m.Subject}");
                }

                return Ok;
            default:
                await stderr.WriteLineAsync(Usage);
                return UsageError;
        }
    }

    /// <summary>The admin CLI retries once (after a fresh reload) on a concurrency conflict.</summary>
    private static async Task<T> RetryOnceAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (EventStoreException ex) when (ex.Failure.Kind == EventStoreFailureKind.ConcurrencyConflict)
        {
            return await action();
        }
    }

    private static Dictionary<string, string>? ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var i = Array.FindIndex(args, a => a.StartsWith("--", StringComparison.Ordinal));
        if (i < 0)
        {
            return result;
        }

        for (; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length)
            {
                return null;
            }

            result[args[i][2..]] = args[i + 1];
        }

        return result;
    }
}
