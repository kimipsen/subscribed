namespace Subscribed.Api.Configuration;

/// <summary>
/// Configuration sources shared by the web host and the admin CLI: appsettings (non-secret
/// defaults), environment variables, then key-per-file secrets (Docker secrets). File names use
/// "__" as the section separator, e.g. <c>/run/secrets/Oidc__ClientSecret</c>.
/// </summary>
public static class ConfigurationSources
{
    public const string SecretsDirectoryVariable = "SUBSCRIBED_SECRETS_DIR";
    public const string DefaultSecretsDirectory = "/run/secrets";

    public static void AddSecrets(IConfigurationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var directory = Environment.GetEnvironmentVariable(SecretsDirectoryVariable);
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = DefaultSecretsDirectory;
        }

        if (Directory.Exists(directory))
        {
            builder.AddKeyPerFile(directory, optional: true, reloadOnChange: false);
        }
    }

    /// <summary>Configuration for the admin CLI (no web host is built).</summary>
    public static (IConfiguration Configuration, bool IsDevelopment) BuildForCli()
    {
        var environment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
            ?? Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
            ?? "Production";
        var builder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile($"appsettings.{environment}.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables();
        AddSecrets(builder);
        return (builder.Build(), string.Equals(environment, "Development", StringComparison.Ordinal));
    }
}
