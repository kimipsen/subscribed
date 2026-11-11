using System.Net;
using Subscribed.Platform.Database;
using Subscribed.Platform.Identity;

namespace Subscribed.Api.Configuration;

public enum IdpLogoutMode
{
    ClientId,
    None,
}

/// <summary>Which keys a command needs. The web host needs everything except the migrator.</summary>
[Flags]
public enum ConfigScope
{
    None = 0,
    Web = 1,
    AppDatabase = 2,
    MigratorDatabase = 4,
    AppRole = 8,
    Authority = 16,
}

/// <summary>Validated configuration (design §8). Built only by <see cref="SettingsValidator"/>.</summary>
public sealed class SubscribedSettings
{
    public required Uri PublicOrigin { get; init; }

    /// <summary>scheme://host[:port], the exact value browsers send in Origin.</summary>
    public required string Origin { get; init; }

    public bool IsHttpOrigin => PublicOrigin.Scheme == Uri.UriSchemeHttp;

    public IReadOnlyList<IPAddress> KnownProxies { get; init; } = [];

    public IReadOnlyList<System.Net.IPNetwork> KnownNetworks { get; init; } = [];

    public required string OidcAuthority { get; init; }

    /// <summary>Authority without trailing slash: the issuer stored with every member.</summary>
    public string Issuer => MemberIdentity.NormalizeIssuer(OidcAuthority);

    public required string ClientId { get; init; }

    public required string ClientSecret { get; init; }

    public IdpLogoutMode IdpLogout { get; init; } = IdpLogoutMode.ClientId;

    public required string AppConnectionString { get; init; }

    public string? MigratorConnectionString { get; init; }

    public string Schema { get; init; } = DatabaseIdentifiers.DefaultSchema;

    public string? AppRole { get; init; }

    public string? SpaRoot { get; init; }

    public string SessionCookieName => IsHttpOrigin ? "subscribed.dev" : "__Host-subscribed";
}

public static class SettingsValidator
{
    public const string PublicOriginKey = "App:PublicOrigin";
    public const string KnownProxiesKey = "App:KnownProxies";
    public const string SpaRootKey = "App:SpaRoot";
    public const string AuthorityKey = "Oidc:Authority";
    public const string IdpLogoutKey = "Oidc:IdpLogout";
    public const string ClientIdKey = "Oidc:ClientId";
    public const string ClientSecretKey = "Oidc:ClientSecret";
    public const string AppConnectionKey = "ConnectionStrings:Subscribed";
    public const string MigratorConnectionKey = "ConnectionStrings:SubscribedMigrator";
    public const string SchemaKey = "Database:Schema";
    public const string AppRoleKey = "Database:AppRole";

    /// <summary>Returns the invalid KEYS only (never values) and, when valid, the settings.</summary>
    public static (SubscribedSettings? Settings, IReadOnlyList<string> InvalidKeys) Validate(IConfiguration configuration, bool isDevelopment, ConfigScope scope)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var invalid = new List<string>();
        var web = scope.HasFlag(ConfigScope.Web);

        Uri? origin = null;
        if (web && !TryOrigin(configuration[PublicOriginKey], isDevelopment, out origin))
        {
            invalid.Add(PublicOriginKey);
        }

        var proxies = new List<IPAddress>();
        var networks = new List<System.Net.IPNetwork>();
        if (web && !TryProxies(configuration[KnownProxiesKey], proxies, networks))
        {
            invalid.Add(KnownProxiesKey);
        }

        var authority = configuration[AuthorityKey];
        if ((web || scope.HasFlag(ConfigScope.Authority)) && !IsValidAuthority(authority, isDevelopment))
        {
            invalid.Add(AuthorityKey);
        }

        var idpLogout = IdpLogoutMode.ClientId;
        var idpLogoutRaw = configuration[IdpLogoutKey];
        if (web && !string.IsNullOrEmpty(idpLogoutRaw) && !Enum.TryParse(idpLogoutRaw, ignoreCase: false, out idpLogout))
        {
            invalid.Add(IdpLogoutKey);
        }

        if (web && string.IsNullOrWhiteSpace(configuration[ClientIdKey]))
        {
            invalid.Add(ClientIdKey);
        }

        if (web && string.IsNullOrWhiteSpace(configuration[ClientSecretKey]))
        {
            invalid.Add(ClientSecretKey);
        }

        var appCs = configuration[AppConnectionKey];
        if ((web || scope.HasFlag(ConfigScope.AppDatabase)) && !IsValidConnectionString(appCs))
        {
            invalid.Add(AppConnectionKey);
        }

        var migratorCs = configuration[MigratorConnectionKey];
        if (scope.HasFlag(ConfigScope.MigratorDatabase) && !IsValidConnectionString(migratorCs))
        {
            invalid.Add(MigratorConnectionKey);
        }

        var schema = configuration[SchemaKey];
        if (string.IsNullOrEmpty(schema))
        {
            schema = DatabaseIdentifiers.DefaultSchema;
        }
        else if (!DatabaseIdentifiers.IsValid(schema))
        {
            invalid.Add(SchemaKey);
        }

        var appRole = configuration[AppRoleKey];
        if ((scope.HasFlag(ConfigScope.AppRole) || !string.IsNullOrEmpty(appRole)) && !DatabaseIdentifiers.IsValid(appRole))
        {
            invalid.Add(AppRoleKey);
        }

        if (invalid.Count > 0)
        {
            return (null, invalid);
        }

        var settings = new SubscribedSettings
        {
            PublicOrigin = origin ?? new Uri("https://unused.invalid"),
            Origin = origin is null ? "" : origin.GetLeftPart(UriPartial.Authority),
            KnownProxies = proxies,
            KnownNetworks = networks,
            OidcAuthority = authority ?? "",
            ClientId = configuration[ClientIdKey] ?? "",
            ClientSecret = configuration[ClientSecretKey] ?? "",
            IdpLogout = idpLogout,
            AppConnectionString = appCs ?? "",
            MigratorConnectionString = string.IsNullOrWhiteSpace(migratorCs) ? null : migratorCs,
            Schema = schema,
            AppRole = string.IsNullOrEmpty(appRole) ? null : appRole,
            SpaRoot = configuration[SpaRootKey],
        };
        return (settings, []);
    }

    private static bool TryOrigin(string? value, bool isDevelopment, out Uri? origin)
    {
        origin = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !(uri.Scheme == Uri.UriSchemeHttps || (isDevelopment && uri.Scheme == Uri.UriSchemeHttp))
            || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        origin = new Uri(uri.GetLeftPart(UriPartial.Authority));
        return true;
    }

    private static bool IsValidAuthority(string? value, bool isDevelopment) =>
        value is { Length: > 0 and <= MemberIdentity.MaxIssuerLength }
        && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (isDevelopment && uri.Scheme == Uri.UriSchemeHttp))
        && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment) && string.IsNullOrEmpty(uri.UserInfo);

    private static bool IsValidConnectionString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            var builder = new Npgsql.NpgsqlConnectionStringBuilder(value);
            return !string.IsNullOrEmpty(builder.Host) && !string.IsNullOrEmpty(builder.Username);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryProxies(string? value, List<IPAddress> proxies, List<System.Net.IPNetwork> networks)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        foreach (var item in value.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (item.Contains('/', StringComparison.Ordinal))
            {
                if (!System.Net.IPNetwork.TryParse(item, out var network))
                {
                    return false;
                }

                networks.Add(network);
            }
            else if (IPAddress.TryParse(item, out var address))
            {
                proxies.Add(address);
            }
            else
            {
                return false;
            }
        }

        return true;
    }
}
