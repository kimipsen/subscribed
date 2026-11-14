using Marten;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.HttpOverrides;
using Subscribed.Api.Bff;
using Subscribed.Api.Configuration;
using Subscribed.Api.Hosting;
using Subscribed.Household;
using Subscribed.Subscriptions;
using Subscribed.Platform.DataProtection;
using Subscribed.Platform.EventStore;
using Subscribed.Platform.Identity;
using Subscribed.Platform.Problems;

namespace Subscribed.Api;

public sealed class ConfigurationInvalidException(IReadOnlyList<string> keys)
    : Exception("Invalid configuration keys: " + string.Join(", ", keys))
{
    public IReadOnlyList<string> Keys { get; } = keys;
}

/// <summary>Composition root of the web host. Tests build the same host with TestServer.</summary>
public static class SubscribedApp
{
    public static IReadOnlyList<IEventStoreModule> Modules { get; } = [new HouseholdModule(), new SubscriptionsModule()];

    public static WebApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args });
        ConfigurationSources.AddSecrets(builder.Configuration);
        ConfigureLogging(builder.Logging);
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = 1024 * 1024;
        });
        return builder;
    }

    public static IReadOnlyList<string> Validate(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        return SettingsValidator.Validate(builder.Configuration, builder.Environment.IsDevelopment(), ConfigScope.Web).InvalidKeys;
    }

    /// <summary>JSON console; framework categories that could echo URLs/queries are capped.</summary>
    public static void ConfigureLogging(ILoggingBuilder logging)
    {
        ArgumentNullException.ThrowIfNull(logging);
        logging.ClearProviders();
        logging.AddJsonConsole(o =>
        {
            o.IncludeScopes = false;
            o.UseUtcTimestamp = true;
            o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
        });
        logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        logging.AddFilter("Microsoft.AspNetCore.Authentication", LogLevel.Error);
        logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
        logging.AddFilter("Microsoft.AspNetCore.Server.Kestrel", LogLevel.Error);
        logging.AddFilter("Npgsql", LogLevel.Warning);
        logging.AddFilter("Marten", LogLevel.Warning);
        logging.AddFilter("JasperFx", LogLevel.Warning);
    }

    public static WebApplication Build(WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var (settings, invalid) = SettingsValidator.Validate(builder.Configuration, builder.Environment.IsDevelopment(), ConfigScope.Web);
        if (settings is null)
        {
            throw new ConfigurationInvalidException(invalid);
        }

        var services = builder.Services;
        services.AddSingleton(settings);
        services.TryAddSingletonTimeProvider();
        services.AddSingleton<IDocumentStore>(_ => SubscribedStore.Create(settings.AppConnectionString, settings.Schema, StoreRole.App, Modules));
        services.AddSingleton<HouseholdService>();
        services.AddSingleton<SubscriptionService>();
        services.AddSingleton<MartenXmlRepository>();
        services.AddDataProtection().SetApplicationName("subscribed");
        services.AddOptions<KeyManagementOptions>()
            .Configure<IServiceProvider>((o, sp) => o.XmlRepository = sp.GetRequiredService<MartenXmlRepository>());

        services.AddBffAuthentication(settings);
        services.AddScoped<IAuthorizationHandler, HouseholdMembership>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ProblemAuthorizationResultHandler>();
        services.AddAuthorizationBuilder()
            .AddPolicy(HouseholdMembership.PolicyName, p => p.RequireAuthenticatedUser().AddRequirements(new HouseholdMemberRequirement()))
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().AddRequirements(new HouseholdMemberRequirement()).Build());

        if (settings.KnownProxies.Count > 0 || settings.KnownNetworks.Count > 0)
        {
            services.Configure<ForwardedHeadersOptions>(o =>
            {
                o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                o.KnownProxies.Clear();
                o.KnownIPNetworks.Clear();
                foreach (var proxy in settings.KnownProxies)
                {
                    o.KnownProxies.Add(proxy);
                }

                foreach (var network in settings.KnownNetworks)
                {
                    o.KnownIPNetworks.Add(network);
                }
            });
        }

        var app = builder.Build();
        ConfigurePipeline(app, settings);
        return app;
    }

    private static void TryAddSingletonTimeProvider(this IServiceCollection services)
    {
        if (!services.Any(d => d.ServiceType == typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }
    }

    private static void ConfigurePipeline(WebApplication app, SubscribedSettings settings)
    {
        var loggers = app.Services.GetRequiredService<ILoggerFactory>();
        var security = loggers.CreateLogger(SecurityEvents.Category);
        var requests = loggers.CreateLogger("Subscribed.Requests");
        var spaRoot = string.IsNullOrWhiteSpace(settings.SpaRoot)
            ? Path.Combine(app.Environment.ContentRootPath, "wwwroot")
            : Path.GetFullPath(settings.SpaRoot, app.Environment.ContentRootPath);
        var spa = new SpaHost(spaRoot);

        if (settings.KnownProxies.Count > 0 || settings.KnownNetworks.Count > 0)
        {
            app.UseForwardedHeaders();
        }

        app.Use((ctx, next) => RequestGuards.RequestLogMiddleware(ctx, next, requests));
        app.Use((ctx, next) => RequestGuards.SecurityHeadersMiddleware(ctx, next, settings));
        app.Use((ctx, next) => RequestGuards.ErrorMiddleware(ctx, next, security));
        spa.UseStaticAssets(app);
        app.UseRouting();
        app.Use((ctx, next) => RequestGuards.CsrfMiddleware(ctx, next, settings, security));
        app.UseAuthentication();
        var keyManager = app.Services.GetRequiredService<IKeyManager>();
        var cookieOptions = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>();
        app.Use((ctx, next) => SessionCookie.CleanupMiddleware(ctx, next, settings, keyManager, cookieOptions.Get(SessionCookie.Scheme), security));
        app.UseAuthorization();

        app.MapHealthEndpoints();
        app.MapBffEndpoints();
        app.MapHouseholdEndpoints();
        app.Map("/api/{**rest}", (HttpContext ctx) => Problem.Result(404, ProblemCodes.NotFound, "Not found."))
            .AllowAnonymous().WithMetadata(new ApiNotFoundMarker()).WithOrder(int.MaxValue);
        app.Map("/api", (HttpContext ctx) => Problem.Result(404, ProblemCodes.NotFound, "Not found."))
            .AllowAnonymous().WithMetadata(new ApiNotFoundMarker());
        // Matches file-like paths too, so a missing asset is a 404 (never the deny-by-default 401).
        app.MapFallback("{**path}", spa.FallbackAsync).AllowAnonymous();
    }
}

/// <summary>Authorization never redirects: 401/403 problem+json (design §6.5).</summary>
public sealed class ProblemAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(authorizeResult);
        if (authorizeResult.Succeeded)
        {
            await next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            await Problem.WriteAsync(context, 401, ProblemCodes.Unauthenticated, "Sign in to continue.");
            return;
        }

        await Problem.WriteAsync(context, 403, ProblemCodes.NotAHouseholdMember, "You are not a member of this household.");
    }
}
