using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Subscribed.TestSupport;
using Xunit;
using static Subscribed.Api.Tests.SubscriptionApiTests;

namespace Subscribed.Api.Tests;

/// <summary>
/// The golden files in <c>frontend/src/app/subscriptions/testing/</c> are flushed by the frontend specs and compared here, so a
/// renamed, moved or added field fails both suites. Set <c>UPDATE_GOLDEN=1</c> to rewrite them from the live responses.
/// </summary>
public sealed partial class SubscriptionContractTests(PostgresFixture pg)
{
    private const string Base = "/api/subscriptions";

    /// <summary>Properties the DTOs declare nullable: a null on either side matches any kind there.</summary>
    private static readonly HashSet<string> Nullable = new(StringComparer.Ordinal)
    {
        "category", "notes", "endDate", "effectiveTo", "change", "percent", "defaultCurrency", "catalogId", "logoUrl", "upcomingChange", "paymentOptionId", "label", "unsubscribeUrl", "effectiveUnsubscribeUrl",
    };

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex DatePattern();

    private static string GoldenDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Subscribed.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.GetFullPath(Path.Combine(dir.FullName, "..", "frontend", "src", "app", "subscriptions", "testing"));
    }

    private static string Kind(JsonElement e, string property) => e.ValueKind switch
    {
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.String when IdPattern().IsMatch(e.GetString()!) => "id",
        JsonValueKind.String when DatePattern().IsMatch(e.GetString()!) => "date",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.Null => "null",
        JsonValueKind.Array => "array",
        JsonValueKind.Object => "object",
        _ => throw new InvalidOperationException(property),
    };

    private static bool SameKind(string a, string b) =>
        a == b || (a is "id" or "date" or "string" && b is "id" or "date" or "string");

    /// <summary>Same property names on every object, same nesting, arrays by position, same value kinds.</summary>
    internal static void AssertSameShape(JsonElement golden, JsonElement actual, string path, string property = "")
    {
        var g = Kind(golden, path);
        var a = Kind(actual, path);
        if ((g == "null" || a == "null") && Nullable.Contains(property))
        {
            return;
        }

        Assert.True(SameKind(g, a), $"{path}: golden is {g}, response is {a}");
        switch (golden.ValueKind)
        {
            case JsonValueKind.Object:
                var goldenNames = golden.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
                var actualNames = actual.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
                Assert.True(goldenNames.SequenceEqual(actualNames), $"{path}: golden has [{string.Join(", ", goldenNames)}], response has [{string.Join(", ", actualNames)}]");
                foreach (var p in golden.EnumerateObject())
                {
                    AssertSameShape(p.Value, actual.GetProperty(p.Name), $"{path}.{p.Name}", p.Name);
                }

                break;
            case JsonValueKind.Array:
                Assert.True(golden.GetArrayLength() == actual.GetArrayLength(), $"{path}: golden has {golden.GetArrayLength()} items, response has {actual.GetArrayLength()}");
                for (var i = 0; i < golden.GetArrayLength(); i++)
                {
                    AssertSameShape(golden[i], actual[i], $"{path}[{i}]", property);
                }

                break;
        }
    }

    internal static void Compare(string file, JsonElement actual)
    {
        var path = Path.Combine(GoldenDirectory(), file);
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(actual, Indented) + "\n");
        }

        Assert.True(File.Exists(path), $"Missing golden file {path}. Run with UPDATE_GOLDEN=1.");
        AssertSameShape(JsonElement.Parse(File.ReadAllText(path)), actual, file);
    }

    [Fact]
    public void The_comparator_catches_added_renamed_and_retyped_fields()
    {
        var golden = JsonElement.Parse("""{"a":"x","b":{"c":[1,2]},"category":null}""");
        AssertSameShape(golden, JsonElement.Parse("""{"a":"y","b":{"c":[3,4]},"category":"Streaming"}"""), "t");
        Assert.ThrowsAny<Exception>(() => AssertSameShape(golden, JsonElement.Parse("""{"a":"y","b":{"c":[3,4]},"category":null,"extra":1}"""), "t"));
        Assert.ThrowsAny<Exception>(() => AssertSameShape(golden, JsonElement.Parse("""{"z":"y","b":{"c":[3,4]},"category":null}"""), "t"));
        Assert.ThrowsAny<Exception>(() => AssertSameShape(golden, JsonElement.Parse("""{"a":1,"b":{"c":[3,4]},"category":null}"""), "t"));
        Assert.ThrowsAny<Exception>(() => AssertSameShape(golden, JsonElement.Parse("""{"a":"y","b":{"c":[3]},"category":null}"""), "t"));
        Assert.ThrowsAny<Exception>(() => AssertSameShape(JsonElement.Parse("""{"a":"x"}"""), JsonElement.Parse("""{"a":null}"""), "t"));
    }

    [Fact]
    public async Task The_catalog_matches_the_golden_file()
    {
        // The standard test catalog has a plan with an upcoming change, a plan without one, and a service with zero plans.
        await using var host = await ApiTestHost.StartAsync(pg, services: s => s.AddSingleton(TestCatalog.Standard()));
        await host.InitHouseholdAsync("subject-owner");
        (await host.LoginAsync("subject-owner")).Dispose();
        using var response = await host.SendAsync(HttpMethod.Get, Base + "/catalog");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Compare("catalog.json", await ApiTestHost.JsonAsync(response));
    }

    [Fact]
    public async Task The_four_scenarios_match_the_golden_files()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        await host.InitHouseholdAsync("subject-owner");
        (await host.LoginAsync("subject-owner")).Dispose();

        // Detail: FilmPlus with the EX-007 history, one upcoming entry, a category, notes and a user unsubscribe URL.
        var film = await AddAsync(host, NewBody(host, "FilmPlus", amount: "59.00", start: Day(host, -1000), category: "Streaming", notes: "Family plan", unsubscribeUrl: "https://example.com/cancel"));
        var filmId = film.GetProperty("id").GetString()!;
        var s1 = await SendOkAsync(host, HttpMethod.Post, $"{Base}/{filmId}/prices", new { effectiveFrom = Day(host, -700), amount = "69.00", cadenceUnit = "month", cadenceCount = 1 }, ETag(film));
        var s2 = await SendOkAsync(host, HttpMethod.Post, $"{Base}/{filmId}/prices", new { effectiveFrom = Day(host, -300), amount = "79.00", cadenceUnit = "month", cadenceCount = 1 }, ETag(s1));
        await SendOkAsync(host, HttpMethod.Post, $"{Base}/{filmId}/prices", new { effectiveFrom = Day(host, 30), amount = "89.00", cadenceUnit = "month", cadenceCount = 1 }, ETag(s2));
        Compare("detail.json", await GetAsync(host, filmId));

        // Overview: one row of each kind.
        var stream = await AddAsync(host, NewBody(host, "StreamCo", start: Day(host, -300), category: "Streaming"));
        await SendOkAsync(host, HttpMethod.Post, $"{Base}/{stream.GetProperty("id").GetString()}/prices", new { effectiveFrom = Day(host, 49), amount = "149.00", cadenceUnit = "month", cadenceCount = 1 }, ETag(stream));
        await AddAsync(host, NewBody(host, "KidsClub", amount: "60.00", start: Day(host, 18)));
        await AddAsync(host, NewBody(host, "SportsLive", amount: "149.00", start: Day(host, -400), end: Day(host, -30)));
        await AddAsync(host, NewBody(host, "VideoTube", "EUR", "11.99", start: Day(host, -200)));
        Compare("overview.json", await OverviewAsync(host));

        // Options.
        using (var options = await host.SendAsync(HttpMethod.Get, Base + "/options"))
        {
            Assert.Equal(HttpStatusCode.OK, options.StatusCode);
            Compare("options.json", await ApiTestHost.JsonAsync(options));
        }

        // Validation problem.
        using (var invalid = await host.SendAsync(HttpMethod.Post, Base, rawBody: """{"name":"","currency":"DKK","startDate":"2024-03-01","amount":"-5.00","cadenceUnit":"month","cadenceCount":1}"""))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Compare("validation-problem.json", await ApiTestHost.JsonAsync(invalid));
        }

        // The overview fixture must hold a not-started row with no upcoming change and a row with one (EX-009).
        var rows = (await OverviewAsync(host)).GetProperty("subscriptions").EnumerateArray().ToDictionary(r => r.GetProperty("name").GetString()!);
        Assert.False(rows["KidsClub"].GetProperty("upcomingPriceChange").GetBoolean());
        Assert.True(rows["StreamCo"].GetProperty("upcomingPriceChange").GetBoolean());
        Assert.Equal("not-started", rows["KidsClub"].GetProperty("status").GetString());
        Assert.Equal("ended", rows["SportsLive"].GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_paused_subscription_has_the_golden_detail_and_row_shape_and_the_409_problem_is_pinned()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        await host.InitHouseholdAsync("subject-owner");
        (await host.LoginAsync("subject-owner")).Dispose();

        var added = await AddAsync(host, NewBody(host, "MusicBox", start: Day(host, -50), category: "Music", notes: "Duo"));
        var id = added.GetProperty("id").GetString()!;
        var paused = await SendOkAsync(host, HttpMethod.Post, $"{Base}/{id}/pause", null, ETag(added));
        Assert.Equal("paused", paused.GetProperty("status").GetString());
        Assert.True(paused.GetProperty("paused").GetBoolean());

        // The detail has the golden detail's property names (its history is a different length), and a paused row matches a golden overview row.
        var goldenDetail = JsonElement.Parse(File.ReadAllText(Path.Combine(GoldenDirectory(), "detail.json")));
        Assert.Equal(goldenDetail.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal), paused.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        AssertSameShape(goldenDetail.GetProperty("priceHistory")[0], paused.GetProperty("priceHistory")[0], "paused-detail.priceHistory[0]", "");
        Assert.True(goldenDetail.GetProperty("paused").ValueKind is JsonValueKind.True or JsonValueKind.False);
        var row = (await OverviewAsync(host)).GetProperty("subscriptions")[0];
        var golden = JsonElement.Parse(File.ReadAllText(Path.Combine(GoldenDirectory(), "overview.json")));
        AssertSameShape(golden.GetProperty("subscriptions")[0], row, "paused-row", "");
        Assert.Equal(paused.GetProperty("version").GetInt64(), row.GetProperty("version").GetInt64());

        // The 409 problem: status, type (the code), title, a trace id, and currentVersion, with the same property set as the 412.
        using var conflict = await host.SendAsync(HttpMethod.Post, $"{Base}/{id}/pause", ifMatch: ETag(paused));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("application/problem+json", conflict.Content.Headers.ContentType?.MediaType);
        var problem = await ApiTestHost.JsonAsync(conflict);
        Assert.Equal(409, problem.GetProperty("status").GetInt32());
        Assert.Equal("urn:subscribed:problem:invalid-subscription-state", problem.GetProperty("type").GetString());
        Assert.False(string.IsNullOrEmpty(problem.GetProperty("traceId").GetString()));
        Assert.Equal("The subscription is not in a state that allows this change.", problem.GetProperty("title").GetString());
        Assert.Equal(paused.GetProperty("version").GetInt64(), problem.GetProperty("currentVersion").GetInt64());
        var names = problem.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
        var concurrency = new List<string>();
        using (var stale = await host.SendAsync(HttpMethod.Post, $"{Base}/{id}/pause", ifMatch: ETag(added)))
        {
            concurrency = [.. (await ApiTestHost.JsonAsync(stale)).EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];
        }

        Assert.Equal(concurrency, names);
    }
}
