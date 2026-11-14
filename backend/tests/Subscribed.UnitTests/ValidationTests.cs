using Marten.Exceptions;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Subscribed.Api.Bff;
using Subscribed.Api.Configuration;
using Subscribed.Household;
using Subscribed.Platform.Database;
using Subscribed.Platform.EventStore;
using Xunit;

namespace Subscribed.UnitTests;

public sealed class ValidationTests
{
    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("/", "/")]
    [InlineData("/household", "/household")]
    [InlineData("/household?x=1", "/household?x=1")]
    [InlineData("//evil.example", null)]
    [InlineData("/\\evil.example", null)]
    [InlineData("https://evil.example", null)]
    [InlineData("household", null)]
    [InlineData("/a\u0000b", null)]
    [InlineData("/a\tb", null)]
    public void Return_url_rules(string? input, string? expected) => Assert.Equal(expected, ReturnUrl.Normalize(input));

    [Fact]
    public void Return_url_max_length_is_512()
    {
        Assert.True(ReturnUrl.IsValid("/" + new string('a', 511)));
        Assert.False(ReturnUrl.IsValid("/" + new string('a', 512)));
    }

    [Fact]
    public void Display_name_is_sanitized_and_truncated_without_splitting_surrogates()
    {
        Assert.Equal("Signed-in user", OidcSetup.SanitizeDisplayName(null));
        Assert.Equal("Signed-in user", OidcSetup.SanitizeDisplayName(" \u0001 "));
        Assert.Equal("Ab", OidcSetup.SanitizeDisplayName(" A\u0007b\n "));
        Assert.Equal(64, OidcSetup.SanitizeDisplayName(new string('x', 5000)).Length);
        var emoji = new string('x', 63) + "😀"; // the pair would straddle position 64
        var result = OidcSetup.SanitizeDisplayName(emoji);
        Assert.Equal(63, result.Length);
        Assert.False(char.IsHighSurrogate(result[^1]));
    }

    [Fact]
    public void ETag_parsing_accepts_only_quoted_versions()
    {
        Assert.True(HouseholdEndpoints.TryParseETag("\"3\"", out var v));
        Assert.Equal(3, v);
        Assert.False(HouseholdEndpoints.TryParseETag("3", out _));
        Assert.False(HouseholdEndpoints.TryParseETag("W/\"3\"", out _));
        Assert.False(HouseholdEndpoints.TryParseETag("\"-1\"", out _));
        Assert.False(HouseholdEndpoints.TryParseETag("\"\"", out _));
        Assert.Equal("\"12\"", HouseholdEndpoints.ETag(12));
    }

    private static Dictionary<string, string?> ValidWeb() => new()
    {
        ["App:PublicOrigin"] = "https://app.example.invalid",
        ["Oidc:Authority"] = "https://idp.example.invalid/realms/x",
        ["Oidc:ClientId"] = "client",
        ["Oidc:ClientSecret"] = "secret-value",
        ["ConnectionStrings:Subscribed"] = "Host=db;Username=app;Password=pw",
    };

    private static IReadOnlyList<string> Invalid(Dictionary<string, string?> values, bool development = false, ConfigScope scope = ConfigScope.Web) =>
        SettingsValidator.Validate(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), development, scope).InvalidKeys;

    [Fact]
    public void Valid_web_configuration_produces_settings()
    {
        var (settings, invalid) = SettingsValidator.Validate(new ConfigurationBuilder().AddInMemoryCollection(ValidWeb()).Build(), false, ConfigScope.Web);
        Assert.Empty(invalid);
        Assert.Equal("https://app.example.invalid", settings!.Origin);
        Assert.Equal("__Host-subscribed", settings.SessionCookieName);
        Assert.Equal(IdpLogoutMode.ClientId, settings.IdpLogout);
        Assert.Equal("subscribed", settings.Schema);
    }

    [Theory]
    [InlineData("App:PublicOrigin", "http://app.example.invalid")]
    [InlineData("App:PublicOrigin", "https://app.example.invalid/path")]
    [InlineData("App:PublicOrigin", "https://app.example.invalid/?q=1")]
    [InlineData("App:PublicOrigin", "not a uri")]
    [InlineData("App:KnownProxies", "10.0.0.1, nope")]
    [InlineData("Oidc:Authority", "http://idp.example.invalid")]
    [InlineData("Oidc:IdpLogout", "Sometimes")]
    [InlineData("Oidc:ClientSecret", "")]
    [InlineData("ConnectionStrings:Subscribed", "")]
    [InlineData("Database:Schema", "Public")]
    [InlineData("Database:Schema", "x; drop")]
    [InlineData("Database:AppRole", "bad-role")]
    public void Invalid_values_are_reported_by_key_only(string key, string value)
    {
        var config = ValidWeb();
        config[key] = value;
        var invalid = Invalid(config);
        Assert.Equal([key], invalid);
    }

    [Fact]
    public void Authority_longer_than_200_chars_is_invalid()
    {
        var config = ValidWeb();
        config["Oidc:Authority"] = "https://idp.example.invalid/" + new string('r', 180);
        Assert.Equal(["Oidc:Authority"], Invalid(config));
    }

    [Fact]
    public void Http_origins_are_allowed_only_in_development_and_use_the_dev_cookie()
    {
        var config = ValidWeb();
        config["App:PublicOrigin"] = "http://localhost:4200";
        config["Oidc:Authority"] = "http://localhost:8180/realms/dev";
        Assert.Equal(["App:PublicOrigin", "Oidc:Authority"], Invalid(config));
        var (settings, invalid) = SettingsValidator.Validate(new ConfigurationBuilder().AddInMemoryCollection(config).Build(), true, ConfigScope.Web);
        Assert.Empty(invalid);
        Assert.Equal("subscribed.dev", settings!.SessionCookieName);
        Assert.Equal("http://localhost:4200", settings.Origin);
    }

    [Fact]
    public void Known_proxies_accept_addresses_and_cidr()
    {
        var config = ValidWeb();
        config["App:KnownProxies"] = "10.0.0.1, 172.16.0.0/12;::1";
        var (settings, _) = SettingsValidator.Validate(new ConfigurationBuilder().AddInMemoryCollection(config).Build(), false, ConfigScope.Web);
        Assert.Equal(2, settings!.KnownProxies.Count);
        Assert.Single(settings.KnownNetworks);
    }

    [Fact]
    public void Migrate_scope_requires_migrator_connection_and_app_role_only()
    {
        var invalid = Invalid(new Dictionary<string, string?>(), scope: ConfigScope.MigratorDatabase | ConfigScope.AppRole);
        Assert.Equal(["ConnectionStrings:SubscribedMigrator", "Database:AppRole"], invalid);
    }

    [Fact]
    public void Grants_file_parses_into_expected_privileges_without_update_or_delete_on_events()
    {
        var expected = Grants.Expected("subscribed");
        Assert.Contains(new ExpectedGrant(GrantObjectKind.Table, "subscribed.mt_events", "SELECT"), expected);
        Assert.Contains(new ExpectedGrant(GrantObjectKind.Table, "subscribed.mt_events", "INSERT"), expected);
        Assert.Contains(new ExpectedGrant(GrantObjectKind.Sequence, "subscribed.mt_events_sequence", "USAGE"), expected);
        Assert.Contains(new ExpectedGrant(GrantObjectKind.Schema, "subscribed", "USAGE"), expected);
        Assert.DoesNotContain(expected, g => g.ObjectName == "subscribed.mt_events" && g.Privilege is "UPDATE" or "DELETE" or "TRUNCATE");
        Assert.DoesNotContain(expected, g => g.Privilege is "DELETE" or "TRUNCATE" or "REFERENCES" or "TRIGGER" or "CREATE");
        foreach (var privilege in new[] { "SELECT", "INSERT", "UPDATE" })
        {
            Assert.Contains(new ExpectedGrant(GrantObjectKind.Table, "subscribed.mt_doc_subscription_view", privilege), expected);
        }

        Assert.Equal(14, expected.Count(g => g.Kind != GrantObjectKind.Schema && g.ObjectName.StartsWith("subscribed.mt_", StringComparison.Ordinal)));
    }

    [Fact]
    public void Grants_render_rejects_unsafe_identifiers_and_substitutes_tokens()
    {
        Assert.Throws<ArgumentException>(() => Grants.Render("subscribed; drop", "app"));
        Assert.Throws<ArgumentException>(() => Grants.Render("subscribed", "App"));
        var statements = Grants.RenderStatements("s1", "r1");
        Assert.All(statements, s => Assert.DoesNotContain("__", s, StringComparison.Ordinal));
        Assert.Contains("GRANT SELECT, INSERT ON TABLE s1.mt_events TO r1;", statements);
    }

    [Fact]
    public void Error_map_classifies_library_conflicts_privileges_and_unavailability()
    {
        Assert.Equal(EventStoreFailureKind.ConcurrencyConflict, EventStoreErrors.Map(new JasperFx.Events.EventStreamUnexpectedMaxEventIdException("s", typeof(object), 1, 2))?.Kind);
        Assert.Equal(EventStoreFailureKind.ConcurrencyConflict, EventStoreErrors.Map(new ExistingStreamIdCollisionException("s", typeof(object)))?.Kind);
        Assert.Equal(EventStoreFailureKind.DatabaseUnavailable, EventStoreErrors.Map(new NpgsqlException("connection refused"))?.Kind);
        Assert.Equal(EventStoreFailureKind.DatabaseUnavailable, EventStoreErrors.Map(new InvalidOperationException("outer", new NpgsqlException("x")))?.Kind);
        Assert.Null(EventStoreErrors.Map(new InvalidOperationException("anything else")));
    }
}
