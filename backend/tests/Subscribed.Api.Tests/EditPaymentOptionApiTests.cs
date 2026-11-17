using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Subscribed.TestSupport;
using Xunit;

namespace Subscribed.Api.Tests;

/// <summary>
/// The payment option on the Edit form (#39): <c>PUT /api/subscriptions/{id}</c> with an optional <c>paymentOption: { id }</c>, one
/// decision and one append together with the details. Runs against the real Postgres fixture.
/// </summary>
public sealed class EditPaymentOptionApiTests(PostgresFixture pg)
{
    private const string Options = "/api/payment-options";
    private const string Subs = "/api/subscriptions";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<ApiTestHost> HostAsync()
    {
        var host = await new SubscriptionApiTests(pg).MemberHostAsync();

        // The host starts at the real clock. Keep every test clear of UTC midnight so "today" cannot change mid-test.
        var now = host.Time.GetUtcNow();
        if (now.TimeOfDay > TimeSpan.FromHours(23.9) || now.TimeOfDay < TimeSpan.FromMinutes(6))
        {
            host.Time.SetUtcNow(new DateTimeOffset(now.UtcDateTime.Date.AddHours(now.TimeOfDay < TimeSpan.FromHours(12) ? 12 : 36), TimeSpan.Zero));
        }

        return host;
    }

    private static async Task<JsonElement> CreateOptionAsync(ApiTestHost host, string name)
    {
        using var response = await host.SendAsync(HttpMethod.Post, Options, new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ApiTestHost.JsonAsync(response);
    }

    private static string IdOf(JsonElement element) => element.GetProperty("id").GetString()!;

    private static string? OptionOf(JsonElement row) => row.GetProperty("paymentOptionId").GetString();

    private static async Task RemoveOptionAsync(ApiTestHost host, JsonElement option)
    {
        using var response = await host.SendAsync(HttpMethod.Delete, $"{Options}/{IdOf(option)}", null, $"\"{option.GetProperty("version").GetInt64()}\"");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static Task<JsonElement> CreateAsync(ApiTestHost host, string? optionId = null, string? end = null, string? start = null) =>
        SubscriptionApiTests.AddAsync(host, new
        {
            name = "StreamCo",
            category = (string?)null,
            notes = (string?)null,
            currency = "DKK",
            startDate = start ?? SubscriptionApiTests.Day(host, -100),
            endDate = end,
            amount = "129.00",
            cadenceUnit = "month",
            cadenceCount = 1,
            paymentOptionId = optionId,
        });

    /// <summary>The details body exactly as stored, with <c>paymentOption</c> set to the given raw JSON, or left out when null.</summary>
    private static string Body(JsonElement detail, string? paymentOptionJson, string? name = null, string? end = null)
    {
        var body = JsonSerializer.SerializeToNode(SubscriptionApiTests.Put(detail, name: name, endDate: end))!.AsObject();
        if (paymentOptionJson is not null)
        {
            body["paymentOption"] = JsonNode.Parse(paymentOptionJson);
        }

        return body.ToJsonString();
    }

    private static string Id(string optionId) => $$"""{"id":"{{optionId}}"}""";

    private static async Task<HttpResponseMessage> PutAsync(ApiTestHost host, JsonElement detail, string? paymentOptionJson, string? name = null, string? end = null) =>
        await host.SendAsync(HttpMethod.Put, $"{Subs}/{detail.GetProperty("id").GetString()}", rawBody: Body(detail, paymentOptionJson, name, end), ifMatch: SubscriptionApiTests.ETag(detail));

    private static async Task<JsonElement> PutOkAsync(ApiTestHost host, JsonElement detail, string? paymentOptionJson, string? name = null)
    {
        using var response = await PutAsync(host, detail, paymentOptionJson, name);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return await ApiTestHost.JsonAsync(response);
    }

    private static async Task AssertUnchangedAsync(ApiTestHost host, JsonElement before)
    {
        var after = await SubscriptionApiTests.GetAsync(host, before.GetProperty("id").GetString()!);
        Assert.Equal(before.GetProperty("version").GetInt64(), after.GetProperty("version").GetInt64());
        Assert.Equal(before.GetProperty("name").GetString(), after.GetProperty("name").GetString());
        Assert.Equal(before.GetProperty("priceHistory").GetArrayLength(), after.GetProperty("priceHistory").GetArrayLength());
    }

    // ---- the gate: an object without an id -------------------------------------------------------

    [Fact]
    public async Task An_empty_payment_option_object_is_a_400_on_payment_option_and_never_clears_the_option()
    {
        await using var host = await HostAsync();
        var visa = await CreateOptionAsync(host, "Visa");
        var created = await CreateAsync(host, IdOf(visa));

        using var response = await PutAsync(host, created, "{}", name: "Renamed");
        var errors = await SubscriptionApiTests.ErrorsAsync(response);
        Assert.Equal(["paymentOption"], errors.EnumerateObject().Select(p => p.Name));
        await AssertUnchangedAsync(host, created);
        Assert.Equal(IdOf(visa), OptionOf((await SubscriptionApiTests.GetAsync(host, created.GetProperty("id").GetString()!)).GetProperty("currentPrice")));
    }

    [Fact]
    public async Task A_non_string_id_is_a_400_not_a_500()
    {
        await using var host = await HostAsync();
        var created = await CreateAsync(host);

        using var response = await PutAsync(host, created, """{"id":5}""");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertUnchangedAsync(host, created);
    }

    // ---- set, change, clear --------------------------------------------------------------------------

    [Fact]
    public async Task Setting_an_option_adds_an_entry_for_today_and_keeps_the_old_entry_as_it_was()
    {
        await using var host = await HostAsync();
        var visa = await CreateOptionAsync(host, "Visa");
        var paypal = await CreateOptionAsync(host, "PayPal");
        var created = await CreateAsync(host, IdOf(visa));

        var updated = await PutOkAsync(host, created, Id(IdOf(paypal)));

        Assert.Equal(created.GetProperty("version").GetInt64() + 2, updated.GetProperty("version").GetInt64());
        Assert.Equal(IdOf(paypal), OptionOf(updated.GetProperty("currentPrice")));
        var history = updated.GetProperty("priceHistory").EnumerateArray().ToList();
        Assert.Equal([IdOf(visa), IdOf(paypal)], history.Select(OptionOf));
        Assert.Equal(SubscriptionApiTests.Day(host, 0), history[1].GetProperty("effectiveFrom").GetString());
        Assert.Equal("129.00", history[1].GetProperty("amount").GetString());
    }

    [Fact]
    public async Task The_details_and_the_option_are_saved_together_with_one_new_version_range()
    {
        await using var host = await HostAsync();
        var visa = await CreateOptionAsync(host, "Visa");
        var created = await CreateAsync(host);

        var updated = await PutOkAsync(host, created, Id(IdOf(visa)), name: "Renamed");

        Assert.Equal("Renamed", updated.GetProperty("name").GetString());
        Assert.Equal(IdOf(visa), OptionOf(updated.GetProperty("currentPrice")));
        Assert.Equal(created.GetProperty("version").GetInt64() + 3, updated.GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task Setting_the_option_on_an_entry_that_starts_today_updates_it_in_place()
    {
        await using var host = await HostAsync();
        var visa = await CreateOptionAsync(host, "Visa");
        var created = await CreateAsync(host, start: SubscriptionApiTests.Day(host, -100));
        var withToday = await SubscriptionApiTests.SendOkAsync(host, HttpMethod.Post, $"{Subs}/{created.GetProperty("id").GetString()}/prices", new
        {
            effectiveFrom = SubscriptionApiTests.Day(host, 0),
            amount = "149.00",
            cadenceUnit = "month",
            cadenceCount = 1,
        }, SubscriptionApiTests.ETag(created));

        var updated = await PutOkAsync(host, withToday, Id(IdOf(visa)));

        Assert.Equal(2, updated.GetProperty("priceHistory").GetArrayLength());
        Assert.Equal(IdOf(visa), OptionOf(updated.GetProperty("currentPrice")));
        Assert.Equal(withToday.GetProperty("version").GetInt64() + 1, updated.GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task Omitting_the_payment_option_leaves_the_options_untouched()
    {
        await using var host = await HostAsync();
        var visa = await CreateOptionAsync(host, "Visa");
        var created = await CreateAsync(host, IdOf(visa));

        var updated = await PutOkAsync(host, created, null, name: "Renamed");

        Assert.Equal(IdOf(visa), OptionOf(updated.GetProperty("currentPrice")));
        Assert.Equal(1, updated.GetProperty("priceHistory").GetArrayLength());
    }

    [Fact]
    public async Task An_explicit_null_payment_option_is_treated_as_omitted()
    {
        await using var host = await HostAsync();
        var visa = await CreateOptionAsync(host, "Visa");
        var created = await CreateAsync(host, IdOf(visa));

        var updated = await PutOkAsync(host, created, "null", name: "Renamed");

        Assert.Equal(IdOf(visa), OptionOf(updated.GetProperty("currentPrice")));
        Assert.Equal(1, updated.GetProperty("priceHistory").GetArrayLength());
    }

    [Fact]
    public async Task An_id_of_null_clears_the_option_going_forward()
    {
        await using var host = await HostAsync();
        var visa = await CreateOptionAsync(host, "Visa");
        var created = await CreateAsync(host, IdOf(visa));

        var updated = await PutOkAsync(host, created, """{"id":null}""");

        Assert.Equal(JsonValueKind.Null, updated.GetProperty("currentPrice").GetProperty("paymentOptionId").ValueKind);
        Assert.Equal([IdOf(visa), null], updated.GetProperty("priceHistory").EnumerateArray().Select(OptionOf));
    }

    [Fact]
    public async Task Saving_with_the_current_option_creates_no_event_and_keeps_the_etag()
    {
        await using var host = await HostAsync();
        var visa = await CreateOptionAsync(host, "Visa");
        var created = await CreateAsync(host, IdOf(visa));

        using var response = await PutAsync(host, created, Id(IdOf(visa)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(SubscriptionApiTests.ETag(created), response.Headers.ETag?.ToString());
        var json = await ApiTestHost.JsonAsync(response);
        Assert.Equal(created.GetProperty("version").GetInt64(), json.GetProperty("version").GetInt64());
        Assert.Equal(1, json.GetProperty("priceHistory").GetArrayLength());
    }

    [Fact]
    public async Task Sending_a_removed_current_option_unchanged_is_a_200_with_no_new_version()
    {
        await using var host = await HostAsync();
        var old = await CreateOptionAsync(host, "Old card");
        var created = await CreateAsync(host, IdOf(old));
        await RemoveOptionAsync(host, old);

        using var response = await PutAsync(host, created, Id(IdOf(old)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(created.GetProperty("version").GetInt64(), (await ApiTestHost.JsonAsync(response)).GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task A_paused_subscription_takes_the_change_like_an_active_one()
    {
        await using var host = await HostAsync();
        var visa = await CreateOptionAsync(host, "Visa");
        var created = await CreateAsync(host);
        var paused = await SubscriptionApiTests.SendOkAsync(host, HttpMethod.Post, $"{Subs}/{created.GetProperty("id").GetString()}/pause", null, SubscriptionApiTests.ETag(created));

        var updated = await PutOkAsync(host, paused, Id(IdOf(visa)));

        Assert.Equal(IdOf(visa), OptionOf(updated.GetProperty("currentPrice")));
        Assert.True(updated.GetProperty("paused").GetBoolean());
    }

    // ---- rejected saves write nothing ----------------------------------------------------------------

    [Fact]
    public async Task A_malformed_id_is_a_400_on_payment_option_id()
    {
        await using var host = await HostAsync();
        var created = await CreateAsync(host);

        using var response = await PutAsync(host, created, """{"id":"not-an-id"}""");
        var errors = await SubscriptionApiTests.ErrorsAsync(response);
        Assert.Equal(["paymentOptionId"], errors.EnumerateObject().Select(p => p.Name));
        await AssertUnchangedAsync(host, created);
    }

    [Fact]
    public async Task A_removed_or_foreign_option_rejects_the_whole_save_including_the_details()
    {
        await using var host = await HostAsync();
        var gone = await CreateOptionAsync(host, "Gone");
        await RemoveOptionAsync(host, gone);
        var created = await CreateAsync(host);
        var foreign = await PaymentOptionApiTests.StartForeignOptionAsync(host);

        foreach (var option in new[] { IdOf(gone), foreign })
        {
            using var response = await PutAsync(host, created, Id(option), name: "Renamed");
            var errors = await SubscriptionApiTests.ErrorsAsync(response);
            Assert.Equal(["paymentOptionId"], errors.EnumerateObject().Select(p => p.Name));
            await AssertUnchangedAsync(host, created);
        }
    }

    [Fact]
    public async Task A_failing_detail_together_with_a_valid_option_stores_no_price_events()
    {
        await using var host = await HostAsync();
        var visa = await CreateOptionAsync(host, "Visa");
        var created = await CreateAsync(host);

        using var response = await PutAsync(host, created, Id(IdOf(visa)), end: SubscriptionApiTests.Day(host, -200));
        var errors = await SubscriptionApiTests.ErrorsAsync(response);
        Assert.True(errors.TryGetProperty("endDate", out _));
        await AssertUnchangedAsync(host, created);
    }

    [Fact]
    public async Task An_ended_subscription_rejects_a_changed_option_but_still_saves_the_details()
    {
        await using var host = await HostAsync();
        var visa = await CreateOptionAsync(host, "Visa");
        var created = await CreateAsync(host, end: SubscriptionApiTests.Day(host, -1));

        using var rejected = await PutAsync(host, created, Id(IdOf(visa)), name: "Renamed");
        var errors = await SubscriptionApiTests.ErrorsAsync(rejected);
        Assert.Equal(["paymentOption"], errors.EnumerateObject().Select(p => p.Name));
        await AssertUnchangedAsync(host, created);

        var renamed = await PutOkAsync(host, created, null, name: "Renamed");
        Assert.Equal("Renamed", renamed.GetProperty("name").GetString());
        Assert.Equal(1, renamed.GetProperty("priceHistory").GetArrayLength());
    }

    [Fact]
    public async Task The_order_of_checks_stays_404_then_412_then_400()
    {
        await using var host = await HostAsync();
        var created = await CreateAsync(host);

        using (var missing = await host.SendAsync(HttpMethod.Put, $"{Subs}/{new string('a', 32)}", rawBody: Body(created, Id(new string('b', 32))), ifMatch: "\"1\""))
        {
            await ApiTestHost.AssertProblemAsync(missing, HttpStatusCode.NotFound, "not-found");
        }

        using (var stale = await host.SendAsync(HttpMethod.Put, $"{Subs}/{created.GetProperty("id").GetString()}", rawBody: Body(created, Id(new string('b', 32))), ifMatch: "\"99\""))
        {
            await ApiTestHost.AssertProblemAsync(stale, HttpStatusCode.PreconditionFailed, "concurrency-conflict");
        }

        using var bad = await PutAsync(host, created, Id(new string('b', 32)));
        await SubscriptionApiTests.ErrorsAsync(bad);
    }

    // ---- observability -------------------------------------------------------------------------------

    [Fact]
    public async Task An_edit_driven_change_writes_one_content_free_log_line()
    {
        await using var host = await HostAsync();
        var visa = await CreateOptionAsync(host, "Visa");
        var created = await CreateAsync(host);

        await PutOkAsync(host, created, Id(IdOf(visa)));

        var line = Assert.Single(host.Logs.Entries, e => e.Contains("subscription.update", StringComparison.Ordinal));
        Assert.Contains("paymentOptionChange=set", line, StringComparison.Ordinal);
        Assert.Contains("priceEvents=2", line, StringComparison.Ordinal);
        Assert.DoesNotContain(IdOf(visa), line, StringComparison.Ordinal);
    }
}
