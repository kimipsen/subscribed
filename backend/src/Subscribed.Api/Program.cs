using Subscribed.Api;
using Subscribed.Api.Admin;

if (args.Length > 0 && args[0] == "admin")
{
    return await AdminCli.RunAsync(args[1..], Console.Out, Console.Error);
}

// Container HEALTHCHECK without curl in the runtime image: GET /health/live on the local port.
if (args.Length > 0 && args[0] == "--probe-live")
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    var port = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")?.Split(';', ',')[0] ?? "8080";
    try
    {
        using var response = await http.GetAsync(new Uri($"http://127.0.0.1:{port}/health/live"));
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        return 1;
    }
}

var checkOnly = args.Contains("--check-config");
var builder = SubscribedApp.CreateBuilder(args.Where(a => a != "--check-config").ToArray());

// Validate before the host starts; report the invalid KEYS only (never values) and exit 1.
var invalid = SubscribedApp.Validate(builder);
if (invalid.Count > 0)
{
    await Console.Error.WriteLineAsync("Invalid configuration keys: " + string.Join(", ", invalid));
    return 1;
}

if (checkOnly)
{
    await Console.Out.WriteLineAsync("Configuration is valid.");
    return 0;
}

var app = SubscribedApp.Build(builder);
await app.RunAsync();
return 0;

/// <summary>Entry point type (referenced by tests).</summary>
public partial class Program;
