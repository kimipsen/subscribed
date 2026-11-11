using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Subscribed.Api.Admin;
using Subscribed.Api.Bff;
using Subscribed.TestSupport;
using Xunit;

namespace Subscribed.Api.Tests;

/// <summary>
/// The real web host (SubscribedApp.Build) on TestServer, a per-test database as the app role, the
/// in-process stub IdP behind the OIDC backchannel, FakeTimeProvider and log capture. Nothing here
/// changes production authentication: the full authorization-code + PKCE flow runs.
/// </summary>
public sealed class ApiTestHost : IAsyncDisposable
{
    public const string PublicOrigin = "https://app.test";
    public static readonly string RunSentinel = "SENTINEL-CLIENT-SECRET-" + Guid.NewGuid().ToString("N")[..12];

    private readonly CookieJarHandler _jar;
    private readonly bool _ownsDatabase;

    private ApiTestHost(TestDatabase db, bool ownsDatabase, StubIdentityProvider stub, WebApplication app, FakeTimeProvider time, LogCapture logs, Dictionary<string, string?> config)
    {
        Db = db;
        _ownsDatabase = ownsDatabase;
        Stub = stub;
        App = app;
        Time = time;
        Logs = logs;
        Configuration = config;
        _jar = new CookieJarHandler(app.GetTestServer().CreateHandler());
        Client = new HttpClient(_jar) { BaseAddress = new Uri(PublicOrigin) };
    }

    public TestDatabase Db { get; }

    public StubIdentityProvider Stub { get; }

    public WebApplication App { get; }

    public FakeTimeProvider Time { get; }

    public LogCapture Logs { get; }

    public HttpClient Client { get; }

    public IReadOnlyDictionary<string, string?> Configuration { get; }

    public CookieJarHandler Jar => _jar;

    public static string Issuer => StubIdentityProvider.Authority;

    public static async Task<ApiTestHost> StartAsync(PostgresFixture pg, Action<Dictionary<string, string?>>? configure = null, TestDatabase? database = null)
    {
        ArgumentNullException.ThrowIfNull(pg);
        var db = database ?? await pg.CreateDatabaseAsync();
        var stub = new StubIdentityProvider(RunSentinel);
        await stub.StartAsync();
        var config = new Dictionary<string, string?>
        {
            ["App:PublicOrigin"] = PublicOrigin,
            ["App:SpaRoot"] = SpaFixture.Root,
            ["Oidc:Authority"] = StubIdentityProvider.Authority,
            ["Oidc:ClientId"] = StubIdentityProvider.ClientId,
            ["Oidc:ClientSecret"] = RunSentinel,
            ["ConnectionStrings:Subscribed"] = db.AppConnectionString,
            ["Database:Schema"] = PostgresFixture.Schema,
        };
        configure?.Invoke(config);

        var builder = SubscribedApp.CreateBuilder(["--environment=Production"]);
        builder.Configuration.AddInMemoryCollection(config);
        builder.WebHost.UseTestServer();
        var logs = new LogCapture();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        builder.Services.AddSingleton<TimeProvider>(time);
        builder.Services.Configure<OpenIdConnectOptions>(OidcSetup.Scheme, o => o.BackchannelHttpHandler = stub.Handler);
        var app = SubscribedApp.Build(builder);
        await app.StartAsync();
        return new ApiTestHost(db, database is null, stub, app, time, logs, config);
    }

    /// <summary>Runs the real admin CLI against this host's database (app role).</summary>
    public async Task<(int Exit, string Out, string Err)> AdminAsync(params string[] args)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(Configuration).Build();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await AdminCli.RunAsync(args, config, false, stdout, stderr, Time);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    public async Task InitHouseholdAsync(string ownerSubject = "subject-owner", string label = "Owner")
    {
        var (exit, _, err) = await AdminAsync("household", "init", "--name", "Synthetic Household", "--owner-subject", ownerSubject, "--owner-label", label);
        Assert.True(exit == 0, err);
    }

    /// <summary>Full browser-style sign-in through the stub IdP. Returns the callback response.</summary>
    public async Task<HttpResponseMessage> LoginAsync(string subject, string? name = "Synthetic User", string returnUrl = "/household", Func<Uri, Uri>? tamperCallback = null, bool deny = false, Action? beforeCallback = null)
    {
        using var login = await Client.GetAsync(new Uri("/bff/login?returnUrl=" + Uri.EscapeDataString(returnUrl), UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var callback = Stub.Authorize(login.Headers.Location!, subject, name, deny: deny);
        if (tamperCallback is not null)
        {
            callback = tamperCallback(callback);
        }

        beforeCallback?.Invoke();
        Assert.Equal("app.test", callback.Host);
        return await Client.GetAsync(new Uri(callback.PathAndQuery, UriKind.Relative), TestContext.Current.CancellationToken);
    }

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? json = null, string? ifMatch = null, bool csrf = true, string? origin = null, string? rawBody = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (csrf)
        {
            request.Headers.Add("X-CSRF", "1");
        }

        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (json is not null)
        {
            request.Content = JsonContent.Create(json);
        }
        else if (rawBody is not null)
        {
            request.Content = new StringContent(rawBody, Encoding.UTF8, "application/json");
        }

        return await Client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return JsonElement.Parse(text);
    }

    public static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        ArgumentNullException.ThrowIfNull(response);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await JsonAsync(response);
        Assert.Equal($"urn:subscribed:problem:{code}", body.GetProperty("type").GetString());
        Assert.Equal((int)status, body.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("traceId").GetString()));
        var raw = body.GetRawText();
        Assert.DoesNotContain("Exception", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Host=", raw, StringComparison.Ordinal);
    }

    /// <summary>Every value that must never appear in a log line (AC15).</summary>
    public IEnumerable<string> ForbiddenLogValues()
    {
        yield return RunSentinel;
        yield return Db.Fixture.AppPassword;
        yield return Db.Fixture.MigratorPassword;
        yield return Db.AppConnectionString;
        yield return Db.MigratorConnectionString;
        yield return "Password=";
        yield return "/signin-oidc?";
        foreach (var secret in Stub.SeenSecrets)
        {
            yield return secret;
        }

        foreach (var cookie in _jar.SeenCookieValues)
        {
            yield return cookie;
        }
    }

    public async ValueTask DisposeAsync()
    {
        var leaked = Logs.Find(ForbiddenLogValues());
        var cors = _jar.Responses.Where(h => h.Any(k => k.StartsWith("Access-Control-Allow", StringComparison.OrdinalIgnoreCase))).ToList();
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
        await Stub.DisposeAsync();
        if (_ownsDatabase)
        {
            await Db.DisposeAsync();
        }

        Assert.True(leaked.Count == 0, "Forbidden values found in logs (AC15).");
        Assert.Empty(cors);
    }
}

/// <summary>Minimal RFC 6265 cookie jar that also records every response's header names.</summary>
public sealed class CookieJarHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private readonly ConcurrentDictionary<string, (string Value, string Path)> _cookies = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _seen = new();
    private readonly ConcurrentQueue<IReadOnlyList<string>> _responses = new();

    public IReadOnlyCollection<string> SeenCookieValues => _seen.ToArray();

    public IReadOnlyCollection<IReadOnlyList<string>> Responses => _responses.ToArray();

    public bool Has(string name) => _cookies.ContainsKey(name);

    public string? Get(string name) => _cookies.TryGetValue(name, out var c) ? c.Value : null;

    public void Set(string name, string value, string path = "/") => _cookies[name] = (value, path);

    public void Remove(string name) => _cookies.TryRemove(name, out _);

    public IReadOnlyList<string> Names => _cookies.Keys.ToList();

    public static IReadOnlyList<string> SetCookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values.ToList() : [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.IsAbsoluteUri ? request.RequestUri.AbsolutePath : "/" + request.RequestUri.OriginalString.TrimStart('/').Split('?')[0];
        var matching = _cookies.Where(c => path.StartsWith(c.Value.Path, StringComparison.Ordinal)).Select(c => $"{c.Key}={c.Value.Value}").ToList();
        if (matching.Count > 0 && !request.Headers.Contains("Cookie"))
        {
            request.Headers.Add("Cookie", string.Join("; ", matching));
        }

        var response = await base.SendAsync(request, cancellationToken);
        _responses.Enqueue(response.Headers.Select(h => h.Key).Concat(response.Content.Headers.Select(h => h.Key)).ToList());
        foreach (var header in SetCookies(response))
        {
            var parts = header.Split(';', StringSplitOptions.TrimEntries);
            var eq = parts[0].IndexOf('=', StringComparison.Ordinal);
            var name = parts[0][..eq];
            var value = parts[0][(eq + 1)..];
            var cookiePath = parts.Skip(1).FirstOrDefault(p => p.StartsWith("path=", StringComparison.OrdinalIgnoreCase))?[5..] ?? "/";
            var expired = parts.Skip(1).Any(p => p.StartsWith("expires=", StringComparison.OrdinalIgnoreCase)
                && DateTimeOffset.TryParse(p[8..], out var e) && e < DateTimeOffset.UtcNow);
            if (string.IsNullOrEmpty(value) || expired)
            {
                _cookies.TryRemove(name, out _);
            }
            else
            {
                _cookies[name] = (value, cookiePath);
                _seen.Enqueue(value);
            }
        }

        return response;
    }
}

/// <summary>A synthetic built SPA: index.html with the CSP nonce token and one hashed asset.</summary>
public static class SpaFixture
{
    private static readonly Lazy<string> RootPath = new(() =>
    {
        var root = Path.Combine(AppContext.BaseDirectory, "spa-fixture");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "index.html"),
            "<!doctype html><html><head><style nonce=\"__CSP_NONCE__\">body{}</style></head><body><app-root ngCspNonce=\"__CSP_NONCE__\"></app-root><script src=\"main-ABCDEFGH.js\"></script></body></html>");
        File.WriteAllText(Path.Combine(root, "main-ABCDEFGH.js"), "console.log('synthetic');");
        File.WriteAllText(Path.Combine(root, "favicon.ico"), "x");
        return root;
    });

    public static string Root => RootPath.Value;
}

internal static class HeaderExtensions
{
    public static string? Single(this HttpResponseHeaders headers, string name) =>
        headers.TryGetValues(name, out var v) ? string.Join(",", v) : null;
}
