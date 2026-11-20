using Marten;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.HttpOverrides;
using Subscribed.Api.Bff;
using Subscribed.Api.Calendar;
using Subscribed.Api.Configuration;
using Subscribed.Api.Hosting;
using Subscribed.Api.Mcp;
using Subscribed.Household;
using Subscribed.Subscriptions;
using Subscribed.Subscriptions.PaymentOptions;
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
    public const string McpPolicyName = "McpMember";

    public static IReadOnlyList<IEventStoreModule> Modules { get; } = [new HouseholdModule(), new SubscriptionsModule(), new PaymentOptionsModule()];

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
        // The MCP SDK logs the full exception (message and stack) of a failed tool call and of a bad argument. The content-free
        // Subscribed.Mcp lines replace it, so only critical SDK events are kept.
        logging.AddFilter("ModelContextProtocol", LogLevel.Critical);
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
        // Test seams are registered with TryAdd so a test override (added before Build) wins; a plain AddSingleton would beat it.
        services.TryAddSingleton<IHouseholdViewReader>(sp => sp.GetRequiredService<HouseholdService>());
        services.TryAddSingleton<IMcpTokenUseStore, MartenMcpTokenUseStore>();
        services.TryAddSingleton<McpTokenUseTracker>();
        services.TryAddSingleton(new McpOptions(builder.Configuration.GetValue("Mcp:Enabled", true)));
        services.TryAddSingleton<ITimeZoneSource, SystemTimeZoneSource>();
        services.TryAddSingleton(sp => CatalogLoader.LoadEmbedded(sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<SubscriptionService>();
        services.AddSingleton<PaymentOptionService>();
        services.AddSingleton<MartenXmlRepository>();
        services.AddDataProtection().SetApplicationName("subscribed");
        services.AddOptions<KeyManagementOptions>()
            .Configure<IServiceProvider>((o, sp) => o.XmlRepository = sp.GetRequiredService<MartenXmlRepository>());

        services.AddBffAuthentication(settings);
        // The MCP bearer scheme is registered next to the BFF scheme but is never a default: only the McpMember policy lists it, so the
        // session cookie can never authenticate /mcp and a token can never authenticate anything else.
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, McpBearerHandler>(McpBearerHandler.SchemeName, _ => { });
        services.AddHttpContextAccessor();
        services.TryAddSingleton<IMcpToolFault, NoMcpToolFault>();
        services.TryAddSingleton<McpToolRunner>();
        var mcpEnabled = builder.Configuration.GetValue("Mcp:Enabled", true);
        if (mcpEnabled)
        {
            services.AddMcpServer()
                .WithHttpTransport(o => o.Stateless = true)
                .WithTools<SubscriptionReadTools>();
        }

        services.AddScoped<IAuthorizationHandler, HouseholdMembership>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ProblemAuthorizationResultHandler>();
        services.AddAuthorizationBuilder()
            .AddPolicy(HouseholdMembership.PolicyName, p => p.RequireAuthenticatedUser().AddRequirements(new HouseholdMemberRequirement()))
            .AddPolicy(McpPolicyName, p => p.AddAuthenticationSchemes(McpBearerHandler.SchemeName).RequireAuthenticatedUser().AddRequirements(new HouseholdMemberRequirement()))
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
        // Eager validation: a bad catalog fails the process at startup, not the first request.
        var catalog = app.Services.GetRequiredService<Catalog>();
        CatalogLog.LogLoaded(app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(CatalogLog.Category), catalog);
        if (app.Services.GetRequiredService<ITimeZoneSource>().IsMissingTzData())
        {
            HouseholdLog.TimeZoneDataMissing(app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(HouseholdLog.Category));
        }

        // No last-used touch may run against a disposed Marten store: drain them on shutdown, bounded by the touch timeout.
        var tracker = app.Services.GetRequiredService<McpTokenUseTracker>();
        var time = app.Services.GetRequiredService<TimeProvider>();
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            try
            {
                using var cts = new CancellationTokenSource(McpTokenUseTracker.Timeout, time);
                tracker.FlushAsync(cts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // Bounded wait: a stuck touch must not hold up shutdown.
            }
        });

        ConfigurePipeline(app, settings, mcpEnabled);
        return app;
    }

    private static void TryAddSingletonTimeProvider(this IServiceCollection services)
    {
        if (!services.Any(d => d.ServiceType == typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }
    }

    private static void ConfigurePipeline(WebApplication app, SubscribedSettings settings, bool mcpEnabled)
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
        app.Use(McpRequestLimits.BodyCapMiddleware);
        app.Use((ctx, next) => RequestGuards.CsrfMiddleware(ctx, next, settings, security));
        app.UseAuthentication();
        var keyManager = app.Services.GetRequiredService<IKeyManager>();
        var cookieOptions = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>();
        app.Use((ctx, next) => SessionCookie.CleanupMiddleware(ctx, next, settings, keyManager, cookieOptions.Get(SessionCookie.Scheme), security));
        app.UseAuthorization();

        app.MapHealthEndpoints();
        app.MapBffEndpoints();
        app.MapHouseholdEndpoints();
        app.MapSubscriptionEndpoints();
        app.MapPaymentOptionEndpoints();
        app.MapCalendarEndpoints();
        app.Map("/api/{**rest}", (HttpContext ctx) => Problem.Result(404, ProblemCodes.NotFound, "Not found."))
            .AllowAnonymous().WithMetadata(new ApiNotFoundMarker()).WithOrder(int.MaxValue);
        app.Map("/api", (HttpContext ctx) => Problem.Result(404, ProblemCodes.NotFound, "Not found."))
            .AllowAnonymous().WithMetadata(new ApiNotFoundMarker());
        MapMcpEndpoints(app, mcpEnabled);
        // Matches file-like paths too, so a missing asset is a 404 (never the deny-by-default 401).
        app.MapFallback("{**path}", spa.FallbackAsync).AllowAnonymous();
    }

    private static readonly string[] AllMethods = [HttpMethods.Get, HttpMethods.Post, HttpMethods.Put, HttpMethods.Delete, HttpMethods.Patch, HttpMethods.Head, HttpMethods.Options];

    /// <summary>
    /// <c>/mcp</c> before the SPA fallback. With <c>Mcp:Enabled=false</c> every method on <c>/mcp</c> is a plain 404 problem, never the SPA HTML with 200
    /// (MapFallback is anonymous and not method-specific). The 404 maps are anonymous because the fallback policy would otherwise answer 401.
    /// Spike (g): the OAuth discovery paths a client probes after the 401 are a clean 404 too, never the SPA HTML.
    /// </summary>
    private static void MapMcpEndpoints(WebApplication app, bool enabled)
    {
        static IResult NotFound() => Problem.Result(404, ProblemCodes.NotFound, "Not found.");
        if (enabled)
        {
            app.MapMcp("/mcp").RequireAuthorization(McpPolicyName);
            // Stateless Streamable HTTP has no server-to-client stream and no session to end (spike h): GET and DELETE are a 405, after the token check.
            app.MapMethods("/mcp", [HttpMethods.Get, HttpMethods.Delete], (HttpContext ctx) =>
            {
                ctx.Response.Headers.Allow = HttpMethods.Post;
                return Problem.Result(405, ProblemCodes.MethodNotAllowed, "Method not allowed.");
            }).RequireAuthorization(McpPolicyName);
        }
        else
        {
            app.MapMethods("/mcp", AllMethods, NotFound).AllowAnonymous();
        }

        app.MapMethods("/mcp/{**path}", AllMethods, NotFound).AllowAnonymous();
        foreach (var path in new[] { "/.well-known/oauth-protected-resource", "/.well-known/oauth-authorization-server", "/register" })
        {
            app.MapMethods(path, AllMethods, NotFound).AllowAnonymous();
            app.MapMethods(path + "/{**rest}", AllMethods, NotFound).AllowAnonymous();
        }
    }
}

/// <summary>Authorization never redirects: 401/403 problem+json (design §6.5). Under <c>/mcp</c> every failure is one identical 401.</summary>
public sealed class ProblemAuthorizationResultHandler(ILoggerFactory? loggers = null) : IAuthorizationMiddlewareResultHandler
{
    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(authorizeResult);
        if (authorizeResult.Succeeded)
        {
            await next(context);
            return;
        }

        if (policy.AuthenticationSchemes.Contains(McpBearerHandler.SchemeName) || McpRequestLimits.IsMcpPath(context.Request.Path))
        {
            // The framework challenge is replaced by this handler, so the scheme cannot write the 401. A token whose member was removed
            // between the scheme's check and the policy check is the same 401 as every other failure: a 403 would leak which part failed.
            if (context.User.Identity?.IsAuthenticated == true && loggers is not null)
            {
                McpLog.AuthRejected(loggers.CreateLogger(McpLog.Category), McpLog.AuthMemberInactive);
            }

            context.Response.Headers.WWWAuthenticate = "Bearer";
            await Problem.WriteAsync(context, 401, ProblemCodes.Unauthenticated, "Sign in to continue.");
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
