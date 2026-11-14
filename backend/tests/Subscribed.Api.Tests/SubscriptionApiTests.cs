using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Subscribed.Platform.Identity;
using Subscribed.TestSupport;
using Xunit;

namespace Subscribed.Api.Tests;

public sealed class SubscriptionApiTests(PostgresFixture pg)
{
    private const string Base = "/api/subscriptions";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal static DateOnly Today(ApiTestHost host) => DateOnly.FromDateTime(host.Time.GetUtcNow().UtcDateTime);

    internal static string Day(ApiTestHost host, int offset) => Today(host).AddDays(offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal async Task<ApiTestHost> MemberHostAsync()
    {
        var host = await ApiTestHost.StartAsync(pg);
        await host.InitHouseholdAsync("subject-owner");
        (await host.LoginAsync("subject-owner")).Dispose();
        return host;
    }

    internal static async Task SwitchUserAsync(ApiTestHost host, string subject)
    {
        foreach (var name in host.Jar.Names)
        {
            host.Jar.Remove(name);
        }

        (await host.LoginAsync(subject)).Dispose();
    }

    internal static object NewBody(ApiTestHost host, string name = "StreamCo", string currency = "DKK", string amount = "129.00", string unit = "month", int count = 1,
        string? start = null, string? end = null, string? category = null, string? notes = null) =>
        new { name, category, notes, currency, startDate = start ?? Day(host, -100), endDate = end, amount, cadenceUnit = unit, cadenceCount = count };

    internal static async Task<JsonElement> AddAsync(ApiTestHost host, object body)
    {
        using var response = await host.SendAsync(HttpMethod.Post, Base, body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var json = await ApiTestHost.JsonAsync(response);
        Assert.Equal($"/api/subscriptions/{json.GetProperty("id").GetString()}", response.Headers.Location?.ToString());
        Assert.Equal($"\"{json.GetProperty("version").GetInt64()}\"", response.Headers.ETag?.ToString());
        return json;
    }

    internal static async Task<JsonElement> GetAsync(ApiTestHost host, string id)
    {
        using var response = await host.SendAsync(HttpMethod.Get, $"{Base}/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiTestHost.JsonAsync(response);
    }

    internal static async Task<JsonElement> OverviewAsync(ApiTestHost host)
    {
        using var response = await host.SendAsync(HttpMethod.Get, Base);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiTestHost.JsonAsync(response);
    }

    internal static string ETag(JsonElement detail) => $"\"{detail.GetProperty("version").GetInt64()}\"";

    internal static async Task<JsonElement> SendOkAsync(ApiTestHost host, HttpMethod method, string path, object? body, string ifMatch)
    {
        using var response = await host.SendAsync(method, path, body, ifMatch);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await ApiTestHost.JsonAsync(response);
    }

    internal static object Put(JsonElement detail, string? name = null, object? endDate = null, bool keepEnd = true, string? currency = null, string? start = null) => new
    {
        name = name ?? detail.GetProperty("name").GetString(),
        category = detail.GetProperty("category").GetString(),
        notes = detail.GetProperty("notes").GetString(),
        currency = currency ?? detail.GetProperty("currency").GetString(),
        startDate = start ?? detail.GetProperty("startDate").GetString(),
        endDate = keepEnd && endDate is null ? detail.GetProperty("endDate").GetString() : endDate,
    };

    internal static async Task<JsonElement> ErrorsAsync(HttpResponseMessage response, HttpStatusCode status = HttpStatusCode.BadRequest)
    {
        await ApiTestHost.AssertProblemAsync(response, status, "validation-failed");
        return (await ApiTestHost.JsonAsync(response)).GetProperty("errors");
    }

    internal static IEnumerable<(HttpMethod Method, string Path, bool Body)> AllRoutes(string id, string entryId) =>
    [
        (HttpMethod.Get, Base, false),
        (HttpMethod.Get, Base + "/options", false),
        (HttpMethod.Post, Base, true),
        (HttpMethod.Get, $"{Base}/{id}", false),
        (HttpMethod.Put, $"{Base}/{id}", true),
        (HttpMethod.Delete, $"{Base}/{id}", false),
        (HttpMethod.Post, $"{Base}/{id}/prices", true),
        (HttpMethod.Put, $"{Base}/{id}/prices/{entryId}", true),
        (HttpMethod.Delete, $"{Base}/{id}/prices/{entryId}", false),
    ];

    // ---- authorization -----------------------------------------------------------------------------

    [Fact]
    public async Task AC7_anonymous_gets_401_on_every_route()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        await host.InitHouseholdAsync("subject-owner");
        foreach (var (method, path, body) in AllRoutes(new string('a', 32), new string('b', 32)))
        {
            using var response = await host.SendAsync(method, path, body ? new { } : null, "\"1\"");
            await ApiTestHost.AssertProblemAsync(response, HttpStatusCode.Unauthorized, "unauthenticated");
        }
    }

    [Fact]
    public async Task AC7_EX_016_non_members_get_403_everywhere_with_no_data_and_no_existence_oracle()
    {
        await using var host = await MemberHostAsync();
        var created = await AddAsync(host, NewBody(host, "Secret Streaming"));
        var id = created.GetProperty("id").GetString()!;
        var entryId = created.GetProperty("priceHistory")[0].GetProperty("entryId").GetString()!;
        await SwitchUserAsync(host, "subject-stranger");

        var missing = new string('0', 32);
        foreach (var target in new[] { id, missing })
        {
            foreach (var (method, path, body) in AllRoutes(target, entryId))
            {
                using var response = await host.SendAsync(method, path, body ? new { } : null, "\"1\"");
                await ApiTestHost.AssertProblemAsync(response, HttpStatusCode.Forbidden, "not-a-household-member");
                var text = await response.Content.ReadAsStringAsync(Ct);
                Assert.DoesNotContain("Secret", text, StringComparison.Ordinal);
                Assert.DoesNotContain("129", text, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task AC7_a_revoked_member_gets_403_on_the_next_request()
    {
        await using var host = await MemberHostAsync();
        var (exit, _, err) = await host.AdminAsync("household", "grant", "--subject", "subject-second", "--label", "Second");
        Assert.True(exit == 0, err);
        await SwitchUserAsync(host, "subject-second");
        using (var ok = await host.SendAsync(HttpMethod.Get, Base))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        var (revokeExit, _, revokeErr) = await host.AdminAsync("household", "revoke", "--member-id", MemberIdentity.MemberId(ApiTestHost.Issuer, "subject-second"));
        Assert.True(revokeExit == 0, revokeErr);
        using var revoked = await host.SendAsync(HttpMethod.Get, Base);
        await ApiTestHost.AssertProblemAsync(revoked, HttpStatusCode.Forbidden, "not-a-household-member");
    }

    [Fact]
    public void The_fixture_module_list_equals_the_app_module_list()
    {
        Assert.Equal(SubscribedApp.Modules.Select(m => m.GetType()), PostgresFixture.Modules.Select(m => m.GetType()));
    }

    // ---- request guards ----------------------------------------------------------------------------

    [Fact]
    public async Task Csrf_and_origin_are_enforced_on_every_unsafe_subscription_route()
    {
        await using var host = await MemberHostAsync();
        var created = await AddAsync(host, NewBody(host));
        var id = created.GetProperty("id").GetString()!;
        var entryId = created.GetProperty("priceHistory")[0].GetProperty("entryId").GetString()!;
        foreach (var (method, path, body) in AllRoutes(id, entryId).Where(r => r.Method != HttpMethod.Get))
        {
            using var noCsrf = await host.SendAsync(method, path, body ? new { } : null, "\"1\"", csrf: false);
            await ApiTestHost.AssertProblemAsync(noCsrf, HttpStatusCode.Forbidden, "csrf-header-required");
            using var wrongOrigin = await host.SendAsync(method, path, body ? new { } : null, "\"1\"", origin: "https://evil.example");
            await ApiTestHost.AssertProblemAsync(wrongOrigin, HttpStatusCode.Forbidden, "origin-mismatch");
        }

        Assert.Equal(1, (await OverviewAsync(host)).GetProperty("subscriptions").GetArrayLength());
    }

    [Fact]
    public async Task Body_limits_content_type_and_malformed_json()
    {
        await using var host = await MemberHostAsync();
        using (var big = await host.SendAsync(HttpMethod.Post, Base, rawBody: "{\"name\":\"" + new string('a', 17 * 1024) + "\"}"))
        {
            await ApiTestHost.AssertProblemAsync(big, HttpStatusCode.RequestEntityTooLarge, "payload-too-large");
        }

        using (var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Base, UriKind.Relative)) { Content = new StringContent("name=x", Encoding.UTF8, "text/plain") })
        {
            request.Headers.Add("X-CSRF", "1");
            using var wrongType = await host.Client.SendAsync(request, Ct);
            await ApiTestHost.AssertProblemAsync(wrongType, HttpStatusCode.UnsupportedMediaType, "unsupported-media-type");
        }

        // Malformed JSON is keyed by the first property in the JSON path when there is one, otherwise by body.
        using (var truncated = await host.SendAsync(HttpMethod.Post, Base, rawBody: "{\"name\":"))
        {
            Assert.True((await ErrorsAsync(truncated)).TryGetProperty("name", out _));
        }

        using (var garbage = await host.SendAsync(HttpMethod.Post, Base, rawBody: "not json"))
        {
            Assert.True((await ErrorsAsync(garbage)).TryGetProperty("body", out _));
        }

        foreach (var raw in new[] { "null", "[]", "\"x\"", "1", "" })
        {
            using var response = await host.SendAsync(HttpMethod.Post, Base, rawBody: raw);
            Assert.True((await ErrorsAsync(response)).TryGetProperty("body", out _), $"body: '{raw}'");
        }

        Assert.Equal(0, (await OverviewAsync(host)).GetProperty("subscriptions").GetArrayLength());
    }

    // ---- happy paths -------------------------------------------------------------------------------

    private static void AssertAmountsAreStrings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in element.EnumerateObject())
                {
                    if (p.Name is "amount" or "monthly" or "yearly" or "normalizedMonthly" or "normalizedYearly" or "percent")
                    {
                        Assert.True(p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Null, $"{p.Name} must be a string");
                    }

                    AssertAmountsAreStrings(p.Value);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AssertAmountsAreStrings(item);
                }

                break;
        }
    }

    [Fact]
    public async Task EX_001_to_EX_005_add_and_overview_with_string_amounts()
    {
        await using var host = await MemberHostAsync();
        var streamCo = await AddAsync(host, NewBody(host, "StreamCo", start: Day(host, -300), category: "Streaming", notes: "Family plan"));
        Assert.Equal("active", streamCo.GetProperty("status").GetString());
        Assert.Equal("129.00", streamCo.GetProperty("currentPrice").GetProperty("amount").GetString());
        Assert.Equal("129.00", streamCo.GetProperty("normalizedMonthly").GetString());
        Assert.Equal("1548.00", streamCo.GetProperty("normalizedYearly").GetString());
        Assert.Equal(1, streamCo.GetProperty("version").GetInt64());
        Assert.Equal("Streaming", streamCo.GetProperty("category").GetString());
        Assert.Equal("Family plan", streamCo.GetProperty("notes").GetString());

        var musicBox = await AddAsync(host, NewBody(host, "MusicBox Family", amount: "1490.00", unit: "year"));
        Assert.Equal("124.17", musicBox.GetProperty("normalizedMonthly").GetString());
        Assert.Equal("1490.00", musicBox.GetProperty("normalizedYearly").GetString());

        var news = await AddAsync(host, NewBody(host, "NewsDaily", amount: "45.00", unit: "week"));
        Assert.Equal("195.00", news.GetProperty("normalizedMonthly").GetString());
        Assert.Equal("2340.00", news.GetProperty("normalizedYearly").GetString());

        var learn = await AddAsync(host, NewBody(host, "LearnPro", "EUR", "270.00", "month", 3));
        Assert.Equal("90.00", learn.GetProperty("normalizedMonthly").GetString());
        Assert.Equal("1080.00", learn.GetProperty("normalizedYearly").GetString());
        var box = await AddAsync(host, NewBody(host, "BoxKit", "DKK", "30.00", "week", 2));
        Assert.Equal("65.00", box.GetProperty("normalizedMonthly").GetString());
        Assert.Equal("780.00", box.GetProperty("normalizedYearly").GetString());

        var overview = await OverviewAsync(host);
        AssertAmountsAreStrings(overview);
        Assert.Equal(Today(host).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), overview.GetProperty("asOf").GetString());
        var totals = overview.GetProperty("totals");
        Assert.Equal(["DKK", "EUR"], totals.EnumerateArray().Select(t => t.GetProperty("currency").GetString()));
        // DKK: 129.00 + 124.1666… + 195.00 + 65.00 = 513.1666… → 513.17; yearly 1548 + 1490 + 2340 + 780 = 6158.00
        Assert.Equal("513.17", totals[0].GetProperty("monthly").GetString());
        Assert.Equal("6158.00", totals[0].GetProperty("yearly").GetString());
        Assert.Equal(4, totals[0].GetProperty("activeCount").GetInt32());
        Assert.Equal("90.00", totals[1].GetProperty("monthly").GetString());
        Assert.Equal("1080.00", totals[1].GetProperty("yearly").GetString());
        Assert.False(overview.GetRawText().Contains("combined", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EX_005_three_currencies_give_three_separate_total_lines()
    {
        await using var host = await MemberHostAsync();
        await AddAsync(host, NewBody(host, "StreamCo"));
        await AddAsync(host, NewBody(host, "NewsDaily", amount: "45.00", unit: "week"));
        await AddAsync(host, NewBody(host, "VideoTube", "EUR", "11.99"));
        await AddAsync(host, NewBody(host, "AppSuite", "USD", "4.99"));

        var totals = (await OverviewAsync(host)).GetProperty("totals");
        Assert.Equal(["DKK", "EUR", "USD"], totals.EnumerateArray().Select(t => t.GetProperty("currency").GetString()));
        Assert.Equal(("324.00", "3888.00"), (totals[0].GetProperty("monthly").GetString(), totals[0].GetProperty("yearly").GetString()));
        Assert.Equal(("11.99", "143.88"), (totals[1].GetProperty("monthly").GetString(), totals[1].GetProperty("yearly").GetString()));
        Assert.Equal(("4.99", "59.88"), (totals[2].GetProperty("monthly").GetString(), totals[2].GetProperty("yearly").GetString()));
    }

    [Fact]
    public async Task EX_006_two_yearly_subscriptions_total_248_33()
    {
        await using var host = await MemberHostAsync();
        await AddAsync(host, NewBody(host, "A", amount: "1490.00", unit: "year"));
        await AddAsync(host, NewBody(host, "B", amount: "1490.00", unit: "year"));
        var total = (await OverviewAsync(host)).GetProperty("totals")[0];
        Assert.Equal("248.33", total.GetProperty("monthly").GetString());
        Assert.Equal("2980.00", total.GetProperty("yearly").GetString());
    }

    [Fact]
    public async Task The_subscriptions_own_minor_unit_drives_the_amounts_JPY_and_KWD()
    {
        await using var host = await MemberHostAsync();
        var jpy = await AddAsync(host, NewBody(host, "JpyPlan", "JPY", "1490", "year"));
        Assert.Equal(0, jpy.GetProperty("minorUnit").GetInt32());
        Assert.Equal("1490", jpy.GetProperty("currentPrice").GetProperty("amount").GetString());
        Assert.Equal("124", jpy.GetProperty("normalizedMonthly").GetString());
        Assert.Equal("1490", jpy.GetProperty("normalizedYearly").GetString());

        var kwd = await AddAsync(host, NewBody(host, "KwdPlan", "KWD", "1.234", "month"));
        Assert.Equal(3, kwd.GetProperty("minorUnit").GetInt32());
        Assert.Equal("1.234", kwd.GetProperty("currentPrice").GetProperty("amount").GetString());
        Assert.Equal("1.234", kwd.GetProperty("normalizedMonthly").GetString());
        Assert.Equal("14.808", kwd.GetProperty("normalizedYearly").GetString());
    }

    [Fact]
    public async Task EX_011_a_not_started_subscription_is_left_out_of_totals_and_has_no_upcoming_change()
    {
        await using var host = await MemberHostAsync();
        var kids = await AddAsync(host, NewBody(host, "KidsClub", amount: "60.00", start: Day(host, 18)));
        Assert.Equal("not-started", kids.GetProperty("status").GetString());
        Assert.Equal("60.00", kids.GetProperty("currentPrice").GetProperty("amount").GetString());
        var history = kids.GetProperty("priceHistory");
        Assert.True(history[0].GetProperty("current").GetBoolean());
        Assert.True(history[0].GetProperty("upcoming").GetBoolean());

        var overview = await OverviewAsync(host);
        var row = overview.GetProperty("subscriptions")[0];
        Assert.False(row.GetProperty("upcomingPriceChange").GetBoolean());
        Assert.Equal(0, overview.GetProperty("totals").GetArrayLength());
    }

    // ---- validation --------------------------------------------------------------------------------

    public static TheoryData<string, string> AddFailures() => new()
    {
        { """{"name":"","currency":"DKK","startDate":"2024-03-01","amount":"1.00","cadenceUnit":"month","cadenceCount":1}""", "name" },
        { """{"name":"X","currency":"XYZ","startDate":"2024-03-01","amount":"1.00","cadenceUnit":"month","cadenceCount":1}""", "currency" },
        { """{"name":"X","currency":"DKK","startDate":"2024-03-01","amount":"-5.00","cadenceUnit":"month","cadenceCount":1}""", "amount" },
        { """{"name":"X","currency":"DKK","startDate":"2024-03-01","amount":"99.955","cadenceUnit":"month","cadenceCount":1}""", "amount" },
        { """{"name":"X","currency":"JPY","startDate":"2024-03-01","amount":"1200.5","cadenceUnit":"month","cadenceCount":1}""", "amount" },
        { """{"name":"X","currency":"DKK","startDate":"2024-03-01","endDate":"2024-01-01","amount":"1.00","cadenceUnit":"month","cadenceCount":1}""", "endDate" },
        { """{"name":"X","currency":"DKK","startDate":"2024-03-01","amount":"1,00","cadenceUnit":"month","cadenceCount":1}""", "amount" },
        { """{"name":"X","currency":"DKK","startDate":"2024-03-01","amount":"1.00","cadenceUnit":"day","cadenceCount":1}""", "cadenceUnit" },
        { """{"name":"X","currency":"DKK","startDate":"2024-03-01","amount":"1.00","cadenceUnit":"month","cadenceCount":0}""", "cadenceCount" },
        { """{"name":"X","currency":"DKK","startDate":"2024-03-01","amount":"1.00","cadenceUnit":"month","cadenceCount":100}""", "cadenceCount" },
        { """{"name":"X","currency":"DKK","startDate":"03/01/2024","amount":"1.00","cadenceUnit":"month","cadenceCount":1}""", "startDate" },
        { """{"name":"X","currency":"DKK","startDate":"2024-03-01","amount":1.00,"cadenceUnit":"month","cadenceCount":1}""", "amount" },
        { """{"name":"X","currency":"DKK","startDate":"2024-03-01","amount":"1.00","cadenceUnit":"month","cadenceCount":"1"}""", "cadenceCount" },
        { """{"name":"X","currency":"DKK","startDate":"2024-03-01","amount":"1.00","cadenceUnit":"month","cadenceCount":1.5}""", "cadenceCount" },
        { """{"currency":"DKK","startDate":"2024-03-01","amount":"1.00","cadenceUnit":"month","cadenceCount":1}""", "name" },
        { """{"name":null,"currency":"DKK","startDate":"2024-03-01","amount":"1.00","cadenceUnit":"month","cadenceCount":1}""", "name" },
        { """{"name":"X","startDate":"2024-03-01","amount":"1.00","cadenceUnit":"month","cadenceCount":1}""", "currency" },
        { """{"name":"X","currency":"DKK","amount":"1.00","cadenceUnit":"month","cadenceCount":1}""", "startDate" },
        { """{"name":"X","currency":"DKK","startDate":"2024-03-01","cadenceUnit":"month","cadenceCount":1}""", "amount" },
        { """{"name":"X","currency":"DKK","startDate":"2024-03-01","amount":"1.00","cadenceCount":1}""", "cadenceUnit" },
        { """{"name":"X","currency":"DKK","startDate":"2024-03-01","amount":"1.00","cadenceUnit":"month"}""", "cadenceCount" },
        { """{"name":"X","currency":"DKK","startDate":"2024-03-01","amount":"1000000000","cadenceUnit":"month","cadenceCount":1}""", "amount" },
    };

    [Theory]
    [MemberData(nameof(AddFailures))]
    public async Task EX_013_add_matrix_each_case_is_400_with_its_key_and_stores_nothing(string raw, string key)
    {
        await using var host = await MemberHostAsync();
        using var response = await host.SendAsync(HttpMethod.Post, Base, rawBody: raw);
        var errors = await ErrorsAsync(response);
        Assert.True(errors.TryGetProperty(key, out _), errors.GetRawText());
        Assert.Equal(0, (await OverviewAsync(host)).GetProperty("subscriptions").GetArrayLength());
    }

    [Fact]
    public async Task EX_013_name_of_81_characters_is_rejected_and_80_is_accepted()
    {
        await using var host = await MemberHostAsync();
        using (var bad = await host.SendAsync(HttpMethod.Post, Base, NewBody(host, new string('n', 81))))
        {
            Assert.True((await ErrorsAsync(bad)).TryGetProperty("name", out _));
        }

        await AddAsync(host, NewBody(host, new string('n', 80)));
    }

    [Fact]
    public async Task Several_field_errors_come_back_together()
    {
        await using var host = await MemberHostAsync();
        using var response = await host.SendAsync(HttpMethod.Post, Base, rawBody: """{"name":"","currency":"XYZ","startDate":"x","amount":"-1","cadenceUnit":"day","cadenceCount":0}""");
        var keys = (await ErrorsAsync(response)).EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["amount", "cadenceCount", "cadenceUnit", "currency", "name", "startDate"], keys);
    }

    [Fact]
    public async Task EX_013_price_entry_matrix_and_precision_comes_from_the_invariant_step()
    {
        await using var host = await MemberHostAsync();
        var created = await AddAsync(host, NewBody(host, "FilmPlus", amount: "59.00", start: Day(host, -300)));
        var id = created.GetProperty("id").GetString()!;
        var initial = created.GetProperty("priceHistory")[0].GetProperty("entryId").GetString()!;
        var etag = ETag(created);
        var path = $"{Base}/{id}/prices";

        async Task<JsonElement> Rejected(string route, HttpMethod method, object body, string key, string ifMatch)
        {
            using var response = await host.SendAsync(method, route, body, ifMatch);
            var errors = await ErrorsAsync(response);
            Assert.True(errors.TryGetProperty(key, out _), errors.GetRawText());
            return errors;
        }

        // before the start date, on the start date, and after nothing (no end date): the first two fail.
        await Rejected(path, HttpMethod.Post, new { effectiveFrom = Day(host, -301), amount = "60.00", cadenceUnit = "month", cadenceCount = 1 }, "effectiveFrom", etag);
        await Rejected(path, HttpMethod.Post, new { effectiveFrom = Day(host, -300), amount = "60.00", cadenceUnit = "month", cadenceCount = 1 }, "effectiveFrom", etag);
        await Rejected(path, HttpMethod.Post, new { effectiveFrom = Day(host, -10), amount = "99.955", cadenceUnit = "month", cadenceCount = 1 }, "amount", etag);
        await Rejected(path, HttpMethod.Post, new { effectiveFrom = Day(host, -10), amount = "60,00", cadenceUnit = "month", cadenceCount = 1 }, "amount", etag);
        await Rejected(path, HttpMethod.Post, new { effectiveFrom = "nope", amount = "60.00", cadenceUnit = "month", cadenceCount = 1 }, "effectiveFrom", etag);
        await Rejected($"{path}/{initial}", HttpMethod.Delete, new { }, "entryId", etag);

        // The same over-precise request with a stale If-Match is a 412: precision is an invariant, checked after the version.
        using (var stale = await host.SendAsync(HttpMethod.Post, path, new { effectiveFrom = Day(host, -10), amount = "99.955", cadenceUnit = "month", cadenceCount = 1 }, "\"99\""))
        {
            await ApiTestHost.AssertProblemAsync(stale, HttpStatusCode.PreconditionFailed, "concurrency-conflict");
        }

        var after = await GetAsync(host, id);
        Assert.Equal(1, after.GetProperty("version").GetInt64());
        Assert.Equal(1, after.GetProperty("priceHistory").GetArrayLength());

        var added = await SendOkAsync(host, HttpMethod.Post, path, new { effectiveFrom = Day(host, -10), amount = "69.00", cadenceUnit = "month", cadenceCount = 1 }, etag);
        var second = added.GetProperty("priceHistory")[1].GetProperty("entryId").GetString()!;
        await Rejected(path, HttpMethod.Post, new { effectiveFrom = Day(host, -10), amount = "70.00", cadenceUnit = "month", cadenceCount = 1 }, "effectiveFrom", ETag(added));
        await Rejected($"{path}/{second}", HttpMethod.Put, new { effectiveFrom = Day(host, -300), amount = "69.00", cadenceUnit = "month", cadenceCount = 1 }, "effectiveFrom", ETag(added));
        await Rejected($"{path}/{initial}", HttpMethod.Put, new { effectiveFrom = Day(host, -299), amount = "59.00", cadenceUnit = "month", cadenceCount = 1 }, "effectiveFrom", ETag(added));
        Assert.Equal(2, (await GetAsync(host, id)).GetProperty("version").GetInt64());
    }

    // ---- PUT replace semantics ---------------------------------------------------------------------

    [Fact]
    public async Task Put_replaces_the_details_and_omitted_properties_clear_them()
    {
        await using var host = await MemberHostAsync();
        var created = await AddAsync(host, NewBody(host, "StreamCo", category: "Streaming", notes: "Family plan", end: Day(host, 30)));
        var id = created.GetProperty("id").GetString()!;

        // Omitting endDate reinstates; omitting category and notes clears them.
        var replaced = await SendOkAsync(host, HttpMethod.Put, $"{Base}/{id}", new { name = "StreamCo", currency = "DKK", startDate = created.GetProperty("startDate").GetString() }, ETag(created));
        Assert.Equal(JsonValueKind.Null, replaced.GetProperty("endDate").ValueKind);
        Assert.Equal(JsonValueKind.Null, replaced.GetProperty("category").ValueKind);
        Assert.Equal(JsonValueKind.Null, replaced.GetProperty("notes").ValueKind);
        // Details and period both changed: two events, so the version moves by two.
        Assert.Equal(created.GetProperty("version").GetInt64() + 2, replaced.GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task Put_with_a_missing_required_field_is_400_and_an_unchanged_put_keeps_the_etag()
    {
        await using var host = await MemberHostAsync();
        var created = await AddAsync(host, NewBody(host));
        var id = created.GetProperty("id").GetString()!;
        using (var noName = await host.SendAsync(HttpMethod.Put, $"{Base}/{id}", new { currency = "DKK", startDate = created.GetProperty("startDate").GetString() }, ETag(created)))
        {
            Assert.True((await ErrorsAsync(noName)).TryGetProperty("name", out _));
        }

        using var same = await host.SendAsync(HttpMethod.Put, $"{Base}/{id}", Put(created), ETag(created));
        Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        Assert.Equal(ETag(created), same.Headers.ETag?.ToString());
    }

    [Fact]
    public async Task EX_014_start_date_edits_in_both_directions()
    {
        await using var host = await MemberHostAsync();
        var created = await AddAsync(host, NewBody(host, "StreamCo", start: Day(host, -200)));
        var id = created.GetProperty("id").GetString()!;
        var added = await SendOkAsync(host, HttpMethod.Post, $"{Base}/{id}/prices", new { effectiveFrom = Day(host, -50), amount = "149.00", cadenceUnit = "month", cadenceCount = 1 }, ETag(created));

        using (var past = await host.SendAsync(HttpMethod.Put, $"{Base}/{id}", Put(added, start: Day(host, -40)), ETag(added)))
        {
            Assert.True((await ErrorsAsync(past)).TryGetProperty("startDate", out _));
        }

        var moved = await SendOkAsync(host, HttpMethod.Put, $"{Base}/{id}", Put(added, start: Day(host, -400)), ETag(added));
        Assert.Equal(Day(host, -400), moved.GetProperty("startDate").GetString());
        Assert.Equal(Day(host, -400), moved.GetProperty("priceHistory")[0].GetProperty("effectiveFrom").GetString());
    }

    // ---- If-Match ----------------------------------------------------------------------------------

    [Fact]
    public async Task If_match_missing_malformed_and_stale_on_all_five_routes()
    {
        await using var host = await MemberHostAsync();
        var created = await AddAsync(host, NewBody(host, start: Day(host, -300)));
        var id = created.GetProperty("id").GetString()!;
        var withPrice = await SendOkAsync(host, HttpMethod.Post, $"{Base}/{id}/prices", new { effectiveFrom = Day(host, -10), amount = "139.00", cadenceUnit = "month", cadenceCount = 1 }, ETag(created));
        var entry = withPrice.GetProperty("priceHistory")[1].GetProperty("entryId").GetString()!;
        var body = new { effectiveFrom = Day(host, -10), amount = "140.00", cadenceUnit = "month", cadenceCount = 1 };

        var routes = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Put, $"{Base}/{id}", Put(withPrice, name: "Renamed")),
            (HttpMethod.Delete, $"{Base}/{id}", null),
            (HttpMethod.Post, $"{Base}/{id}/prices", new { effectiveFrom = Day(host, -5), amount = "1.00", cadenceUnit = "month", cadenceCount = 1 }),
            (HttpMethod.Put, $"{Base}/{id}/prices/{entry}", body),
            (HttpMethod.Delete, $"{Base}/{id}/prices/{entry}", null),
        };

        foreach (var (method, path, payload) in routes)
        {
            using var missing = await host.SendAsync(method, path, payload);
            await ApiTestHost.AssertProblemAsync(missing, HttpStatusCode.PreconditionRequired, "if-match-required");

            using var malformed = await host.SendAsync(method, path, payload, "3");
            Assert.True((await ErrorsAsync(malformed)).TryGetProperty("ifMatch", out _));

            using var stale = await host.SendAsync(method, path, payload, "\"1\"");
            await ApiTestHost.AssertProblemAsync(stale, HttpStatusCode.PreconditionFailed, "concurrency-conflict");
            var problem = await ApiTestHost.JsonAsync(stale);
            Assert.Equal(withPrice.GetProperty("version").GetInt64(), problem.GetProperty("currentVersion").GetInt64());
            Assert.Equal("The subscription was changed by someone else.", problem.GetProperty("title").GetString());
        }

        var unchanged = await GetAsync(host, id);
        Assert.Equal(withPrice.GetProperty("version").GetInt64(), unchanged.GetProperty("version").GetInt64());
        Assert.Equal("StreamCo", unchanged.GetProperty("name").GetString());
    }

    [Fact]
    public async Task EX_015_the_second_writer_at_the_same_version_gets_412()
    {
        await using var host = await MemberHostAsync();
        var created = await AddAsync(host, NewBody(host));
        var id = created.GetProperty("id").GetString()!;
        await SendOkAsync(host, HttpMethod.Put, $"{Base}/{id}", Put(created, name: "By A"), ETag(created));
        using var b = await host.SendAsync(HttpMethod.Put, $"{Base}/{id}", Put(created, name: "By B"), ETag(created));
        await ApiTestHost.AssertProblemAsync(b, HttpStatusCode.PreconditionFailed, "concurrency-conflict");
        Assert.Equal("By A", (await GetAsync(host, id)).GetProperty("name").GetString());
    }

    // ---- history -----------------------------------------------------------------------------------

    [Fact]
    public async Task EX_007_and_EX_008_history_through_the_api()
    {
        await using var host = await MemberHostAsync();
        var film = await AddAsync(host, NewBody(host, "FilmPlus", amount: "59.00", start: Day(host, -1000)));
        var id = film.GetProperty("id").GetString()!;
        var s1 = await SendOkAsync(host, HttpMethod.Post, $"{Base}/{id}/prices", new { effectiveFrom = Day(host, -700), amount = "69.00", cadenceUnit = "month", cadenceCount = 1 }, ETag(film));
        var s2 = await SendOkAsync(host, HttpMethod.Post, $"{Base}/{id}/prices", new { effectiveFrom = Day(host, -300), amount = "79.00", cadenceUnit = "month", cadenceCount = 1 }, ETag(s1));

        var rows = s2.GetProperty("priceHistory");
        Assert.Equal(["59.00", "69.00", "79.00"], rows.EnumerateArray().Select(r => r.GetProperty("amount").GetString()));
        Assert.Equal(JsonValueKind.Null, rows[0].GetProperty("change").ValueKind);
        Assert.Equal(("10.00", "16.9"), (rows[1].GetProperty("change").GetProperty("amount").GetString(), rows[1].GetProperty("change").GetProperty("percent").GetString()));
        Assert.Equal(("10.00", "14.5"), (rows[2].GetProperty("change").GetProperty("amount").GetString(), rows[2].GetProperty("change").GetProperty("percent").GetString()));
        Assert.Equal([false, false, true], rows.EnumerateArray().Select(r => r.GetProperty("current").GetBoolean()));
        Assert.Equal(Day(host, -701), rows[0].GetProperty("effectiveTo").GetString());
        Assert.Equal(JsonValueKind.Null, rows[2].GetProperty("effectiveTo").ValueKind);
        Assert.Equal("79.00", s2.GetProperty("currentPrice").GetProperty("amount").GetString());

        var series = await AddAsync(host, NewBody(host, "SeriesHub", amount: "99.00", start: Day(host, -600)));
        var sid = series.GetProperty("id").GetString()!;
        var switched = await SendOkAsync(host, HttpMethod.Post, $"{Base}/{sid}/prices", new { effectiveFrom = Day(host, -100), amount = "999.00", cadenceUnit = "year", cadenceCount = 1 }, ETag(series));
        Assert.Equal("83.25", switched.GetProperty("normalizedMonthly").GetString());
        var change = switched.GetProperty("priceHistory")[1].GetProperty("change");
        Assert.Equal(("-15.75", "-15.9"), (change.GetProperty("amount").GetString(), change.GetProperty("percent").GetString()));
    }

    [Fact]
    public async Task BR_014_price_entry_correction_and_removal_return_200_and_a_new_etag()
    {
        await using var host = await MemberHostAsync();
        var film = await AddAsync(host, NewBody(host, "FilmPlus", amount: "59.00", start: Day(host, -1000)));
        var id = film.GetProperty("id").GetString()!;
        var initial = film.GetProperty("priceHistory")[0].GetProperty("entryId").GetString()!;
        var withSecond = await SendOkAsync(host, HttpMethod.Post, $"{Base}/{id}/prices", new { effectiveFrom = Day(host, -700), amount = "69.00", cadenceUnit = "month", cadenceCount = 1 }, ETag(film));
        var second = withSecond.GetProperty("priceHistory")[1].GetProperty("entryId").GetString()!;

        using var corrected = await host.SendAsync(HttpMethod.Put, $"{Base}/{id}/prices/{initial}", new { effectiveFrom = Day(host, -1000), amount = "49.00", cadenceUnit = "month", cadenceCount = 1 }, ETag(withSecond));
        Assert.Equal(HttpStatusCode.OK, corrected.StatusCode);
        var correctedJson = await ApiTestHost.JsonAsync(corrected);
        Assert.NotEqual(ETag(withSecond), corrected.Headers.ETag?.ToString());
        Assert.Equal(corrected.Headers.ETag?.ToString(), ETag(correctedJson));
        Assert.Equal("49.00", correctedJson.GetProperty("priceHistory")[0].GetProperty("amount").GetString());
        Assert.Equal("20.00", correctedJson.GetProperty("priceHistory")[1].GetProperty("change").GetProperty("amount").GetString());

        using var removed = await host.SendAsync(HttpMethod.Delete, $"{Base}/{id}/prices/{second}", ifMatch: ETag(correctedJson));
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        var removedJson = await ApiTestHost.JsonAsync(removed);
        Assert.Equal(1, removedJson.GetProperty("priceHistory").GetArrayLength());
        Assert.NotEqual(ETag(correctedJson), removed.Headers.ETag?.ToString());

        using var unknown = await host.SendAsync(HttpMethod.Delete, $"{Base}/{id}/prices/{second}", ifMatch: ETag(removedJson));
        await ApiTestHost.AssertProblemAsync(unknown, HttpStatusCode.NotFound, "not-found");
    }

    [Fact]
    public async Task EX_009_an_upcoming_price_becomes_current_after_the_clock_passes_it()
    {
        await using var host = await MemberHostAsync();
        var created = await AddAsync(host, NewBody(host, "StreamCo", start: Day(host, -100)));
        var id = created.GetProperty("id").GetString()!;
        var detail = await SendOkAsync(host, HttpMethod.Post, $"{Base}/{id}/prices", new { effectiveFrom = Day(host, 49), amount = "149.00", cadenceUnit = "month", cadenceCount = 1 }, ETag(created));

        Assert.Equal("129.00", detail.GetProperty("currentPrice").GetProperty("amount").GetString());
        Assert.True(detail.GetProperty("priceHistory")[1].GetProperty("upcoming").GetBoolean());
        var before = await OverviewAsync(host);
        Assert.True(before.GetProperty("subscriptions")[0].GetProperty("upcomingPriceChange").GetBoolean());
        Assert.Equal("129.00", before.GetProperty("totals")[0].GetProperty("monthly").GetString());

        host.Time.Advance(TimeSpan.FromDays(50));
        (await host.LoginAsync("subject-owner")).Dispose();

        var after = await OverviewAsync(host);
        Assert.False(after.GetProperty("subscriptions")[0].GetProperty("upcomingPriceChange").GetBoolean());
        Assert.Equal("149.00", after.GetProperty("subscriptions")[0].GetProperty("currentPrice").GetProperty("amount").GetString());
        Assert.Equal("149.00", after.GetProperty("totals")[0].GetProperty("monthly").GetString());
        Assert.False((await GetAsync(host, id)).GetProperty("priceHistory")[1].GetProperty("upcoming").GetBoolean());
    }

    [Fact]
    public async Task EX_010_and_EX_012_status_around_the_inclusive_end_date()
    {
        await using var host = await MemberHostAsync();
        await AddAsync(host, NewBody(host, "Ends today", start: Day(host, -50), end: Day(host, 0)));
        await AddAsync(host, NewBody(host, "Ended yesterday", start: Day(host, -50), end: Day(host, -1)));

        var overview = await OverviewAsync(host);
        var rows = overview.GetProperty("subscriptions").EnumerateArray().ToDictionary(r => r.GetProperty("name").GetString()!);
        Assert.Equal("active", rows["Ends today"].GetProperty("status").GetString());
        Assert.Equal("ended", rows["Ended yesterday"].GetProperty("status").GetString());
        Assert.Equal("129.00", rows["Ended yesterday"].GetProperty("currentPrice").GetProperty("amount").GetString());
        Assert.Equal(1, overview.GetProperty("totals")[0].GetProperty("activeCount").GetInt32());
    }

    [Fact]
    public async Task End_then_reinstate_through_put()
    {
        await using var host = await MemberHostAsync();
        var created = await AddAsync(host, NewBody(host, category: "Streaming", notes: "Keep me"));
        var id = created.GetProperty("id").GetString()!;
        var ended = await SendOkAsync(host, HttpMethod.Put, $"{Base}/{id}", Put(created, endDate: Day(host, -1)), ETag(created));
        Assert.Equal("ended", ended.GetProperty("status").GetString());
        Assert.Equal("Streaming", ended.GetProperty("category").GetString());
        Assert.Equal("Keep me", ended.GetProperty("notes").GetString());
        var reinstated = await SendOkAsync(host, HttpMethod.Put, $"{Base}/{id}", Put(ended, keepEnd: false), ETag(ended));
        Assert.Equal("active", reinstated.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, reinstated.GetProperty("endDate").ValueKind);
    }

    [Fact]
    public async Task EX_017_removal_is_a_204_then_404_and_the_subscription_leaves_the_list_and_totals()
    {
        await using var host = await MemberHostAsync();
        var keep = await AddAsync(host, NewBody(host, "Keep"));
        var gone = await AddAsync(host, NewBody(host, "KidsClub", amount: "60.00"));
        var id = gone.GetProperty("id").GetString()!;

        using (var removed = await host.SendAsync(HttpMethod.Delete, $"{Base}/{id}", ifMatch: ETag(gone)))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        using (var get = await host.SendAsync(HttpMethod.Get, $"{Base}/{id}"))
        {
            await ApiTestHost.AssertProblemAsync(get, HttpStatusCode.NotFound, "not-found");
        }

        // PUT and DELETE on a removed subscription are 404 even with a stale If-Match.
        using (var put = await host.SendAsync(HttpMethod.Put, $"{Base}/{id}", Put(gone), "\"1\""))
        {
            await ApiTestHost.AssertProblemAsync(put, HttpStatusCode.NotFound, "not-found");
        }

        using (var delete = await host.SendAsync(HttpMethod.Delete, $"{Base}/{id}", ifMatch: "\"99\""))
        {
            await ApiTestHost.AssertProblemAsync(delete, HttpStatusCode.NotFound, "not-found");
        }

        var overview = await OverviewAsync(host);
        Assert.Equal([keep.GetProperty("id").GetString()], overview.GetProperty("subscriptions").EnumerateArray().Select(r => r.GetProperty("id").GetString()));
        Assert.Equal("129.00", overview.GetProperty("totals")[0].GetProperty("monthly").GetString());
    }

    [Theory]
    [InlineData("options")]
    [InlineData("new")]
    [InlineData("NOT-AN-ID")]
    [InlineData("0193A0000000000000000000000000AB")]
    public async Task Malformed_ids_are_404_after_authorization(string id)
    {
        await using var host = await MemberHostAsync();
        if (id != "options")
        {
            using var get = await host.SendAsync(HttpMethod.Get, $"{Base}/{id}");
            await ApiTestHost.AssertProblemAsync(get, HttpStatusCode.NotFound, "not-found");
        }

        using var put = await host.SendAsync(HttpMethod.Put, $"{Base}/{id}", new { }, "\"1\"");
        await ApiTestHost.AssertProblemAsync(put, HttpStatusCode.NotFound, "not-found");
    }

    // ---- options and ordering ----------------------------------------------------------------------

    [Fact]
    public async Task Options_list_currencies_by_code_and_the_default_follows_add_edit_and_remove()
    {
        await using var host = await MemberHostAsync();
        using (var first = await host.SendAsync(HttpMethod.Get, Base + "/options"))
        {
            var options = await ApiTestHost.JsonAsync(first);
            Assert.Equal(JsonValueKind.Null, options.GetProperty("defaultCurrency").ValueKind);
            var codes = options.GetProperty("currencies").EnumerateArray().Select(c => c.GetProperty("code").GetString()!).ToList();
            Assert.Equal(codes.Order(StringComparer.Ordinal), codes);
            Assert.Equal(0, options.GetProperty("currencies").EnumerateArray().Single(c => c.GetProperty("code").GetString() == "JPY").GetProperty("minorUnit").GetInt32());
            Assert.Equal(3, options.GetProperty("currencies").EnumerateArray().Single(c => c.GetProperty("code").GetString() == "KWD").GetProperty("minorUnit").GetInt32());
        }

        async Task<string?> DefaultAsync()
        {
            using var response = await host.SendAsync(HttpMethod.Get, Base + "/options");
            return (await ApiTestHost.JsonAsync(response)).GetProperty("defaultCurrency").GetString();
        }

        await AddAsync(host, NewBody(host, "A", "DKK"));
        host.Time.Advance(TimeSpan.FromSeconds(5));
        var b = await AddAsync(host, NewBody(host, "B", "EUR"));
        Assert.Equal("EUR", await DefaultAsync());

        await SendOkAsync(host, HttpMethod.Put, $"{Base}/{b.GetProperty("id").GetString()}", Put(b, currency: "USD"), ETag(b));
        Assert.Equal("USD", await DefaultAsync());

        using (var removed = await host.SendAsync(HttpMethod.Delete, $"{Base}/{b.GetProperty("id").GetString()}", ifMatch: "\"2\""))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        Assert.Equal("DKK", await DefaultAsync());
    }

    [Fact]
    public async Task Same_instant_additions_break_the_default_currency_tie_by_the_highest_id()
    {
        await using var host = await MemberHostAsync();
        var one = await AddAsync(host, NewBody(host, "One", "DKK"));
        var two = await AddAsync(host, NewBody(host, "Two", "EUR"));
        var highest = new[] { one, two }.OrderByDescending(s => s.GetProperty("id").GetString(), StringComparer.Ordinal).First();

        using var response = await host.SendAsync(HttpMethod.Get, Base + "/options");
        Assert.Equal(highest.GetProperty("currency").GetString(), (await ApiTestHost.JsonAsync(response)).GetProperty("defaultCurrency").GetString());
    }

    [Fact]
    public async Task The_list_is_ordered_by_name_then_start_date_then_id()
    {
        await using var host = await MemberHostAsync();
        var same1 = await AddAsync(host, NewBody(host, "Twin", start: Day(host, -10)));
        var same2 = await AddAsync(host, NewBody(host, "Twin", start: Day(host, -10)));
        var earlier = await AddAsync(host, NewBody(host, "Twin", start: Day(host, -20)));
        var other = await AddAsync(host, NewBody(host, "Alpha", start: Day(host, -5)));

        var ids = (await OverviewAsync(host)).GetProperty("subscriptions").EnumerateArray().Select(r => r.GetProperty("id").GetString()!).ToList();
        var pair = new[] { same1, same2 }.Select(s => s.GetProperty("id").GetString()!).Order(StringComparer.Ordinal).ToList();
        Assert.Equal([other.GetProperty("id").GetString()!, earlier.GetProperty("id").GetString()!, pair[0], pair[1]], ids);
    }
}
