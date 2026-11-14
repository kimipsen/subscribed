using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        "category", "notes", "endDate", "effectiveTo", "change", "percent", "defaultCurrency",
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

    private static void Compare(string file, JsonElement actual)
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
    public async Task The_four_scenarios_match_the_golden_files()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        await host.InitHouseholdAsync("subject-owner");
        (await host.LoginAsync("subject-owner")).Dispose();

        // Detail: FilmPlus with the EX-007 history, one upcoming entry, a category and notes.
        var film = await AddAsync(host, NewBody(host, "FilmPlus", amount: "59.00", start: Day(host, -1000), category: "Streaming", notes: "Family plan"));
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
}
