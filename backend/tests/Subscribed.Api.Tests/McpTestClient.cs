using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace Subscribed.Api.Tests;

/// <summary>One HTTP answer from <c>/mcp</c>: status, content type, the raw body and the single JSON-RPC message in it (plain JSON or <c>text/event-stream</c> framing).</summary>
public sealed record McpReply(HttpStatusCode Status, string? ContentType, string Raw, IReadOnlyDictionary<string, string> Headers)
{
    public bool HasHeader(string name) => Headers.ContainsKey(name);

    public string Header(string name) => Headers[name];

    public bool IsEventStream => string.Equals(ContentType, "text/event-stream", StringComparison.OrdinalIgnoreCase);

    /// <summary>The JSON-RPC message. An event stream carries it in <c>data:</c> lines; plain JSON is the body itself.</summary>
    public JsonElement Message
    {
        get
        {
            if (!IsEventStream)
            {
                return JsonElement.Parse(Raw);
            }

            var data = string.Join("\n", Raw.Split('\n').Where(l => l.StartsWith("data:", StringComparison.Ordinal)).Select(l => l["data:".Length..].TrimStart()).Where(l => l.Length > 0));
            return JsonElement.Parse(data);
        }
    }

    public JsonElement Result => Message.GetProperty("result");

    /// <summary>A tool answer: <c>isError</c> and the parsed JSON text of the first content block.</summary>
    public (bool IsError, JsonElement Body) Tool()
    {
        var result = Result;
        var isError = result.TryGetProperty("isError", out var flag) && flag.GetBoolean();
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        return (isError, JsonElement.Parse(text));
    }
}

/// <summary>JSON-RPC over <c>POST /mcp</c> with a bearer token. The helper accepts both response framings, because the SDK decides which one it uses.</summary>
public static class McpTestClient
{
    public const string Path = "/mcp";
    public const string LatestProtocol = "2025-11-25";

    private static int _id;

    public static async Task<string> CreateTokenAsync(ApiTestHost host, string name = "Claude Code")
    {
        ArgumentNullException.ThrowIfNull(host);
        using var response = await host.SendAsync(HttpMethod.Post, "/api/household/mcp-tokens", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ApiTestHost.JsonAsync(response)).GetProperty("secret").GetString()!;
    }

    public static async Task<McpReply> SendAsync(ApiTestHost host, HttpMethod method, string path, string? token, string? body = null, string? authorization = null, Action<HttpRequestMessage>? configure = null, bool useCookies = true)
    {
        ArgumentNullException.ThrowIfNull(host);
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }
        else if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        configure?.Invoke(request);
        using var client = useCookies ? null : new HttpClient(host.App.GetTestServer().CreateHandler()) { BaseAddress = new Uri(ApiTestHost.PublicOrigin) };
        using var response = await (client ?? host.Client).SendAsync(request, TestContext.Current.CancellationToken);
        var raw = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var headers = response.Headers.Concat(response.Content.Headers).ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        return new McpReply(response.StatusCode, response.Content.Headers.ContentType?.MediaType, raw, headers);
    }

    public static string Rpc(string method, object? parameters = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = Interlocked.Increment(ref _id), ["method"] = method, ["params"] = parameters ?? new { } });

    public static Task<McpReply> InitializeAsync(ApiTestHost host, string? token, string protocolVersion = LatestProtocol) =>
        SendAsync(host, HttpMethod.Post, Path, token, Rpc("initialize", new { protocolVersion, capabilities = new { }, clientInfo = new { name = "test", version = "1" } }));

    public static Task<McpReply> ListToolsAsync(ApiTestHost host, string? token) =>
        SendAsync(host, HttpMethod.Post, Path, token, Rpc("tools/list"));

    public static Task<McpReply> CallAsync(ApiTestHost host, string? token, string tool, object? arguments = null) =>
        SendAsync(host, HttpMethod.Post, Path, token, Rpc("tools/call", new { name = tool, arguments = arguments ?? new { } }));
}
