using System.Globalization;
using System.Net;
using System.Text.Json;
using Subscribed.TestSupport;
using Xunit;

namespace Subscribed.Api.Tests;

/// <summary>The <c>cancelBy</c> deadline through the subscription endpoints, rendered in the household zone (#33).</summary>
public sealed class CancelByApiTests(PostgresFixture pg)
{
    private const string Base = "/api/subscriptions";
    private const string Copenhagen = "Europe/Copenhagen";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<ApiTestHost> StartAsync(string? zone = Copenhagen, DateTimeOffset? now = null)
    {
        var host = await ApiTestHost.StartAsync(pg);
        if (now is { } fixedNow)
        {
            host.Time.SetUtcNow(fixedNow);
        }

        await host.InitHouseholdAsync("subject-owner");
        (await host.LoginAsync("subject-owner")).Dispose();
        if (zone is not null)
        {
            using var get = await host.SendAsync(HttpMethod.Get, "/api/household");
            using var put = await host.SendAsync(HttpMethod.Put, "/api/household/time-zone", new { timeZoneId = zone }, get.Headers.ETag!.ToString());
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        }

        return host;
    }

    private static Dictionary<string, object?> AddBody(ApiTestHost host, string? cancelBy, bool includeKey = true, string? start = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = "StreamCo",
            ["category"] = null,
            ["notes"] = null,
            ["currency"] = "DKK",
            ["startDate"] = start ?? SubscriptionApiTests.Day(host, -100),
            ["endDate"] = null,
            ["amount"] = "129.00",
            ["cadenceUnit"] = "month",
            ["cadenceCount"] = 1,
            ["catalogId"] = null,
        };
        if (includeKey)
        {
            body["cancelBy"] = cancelBy;
        }

        return body;
    }

    private static Dictionary<string, object?> PutBody(JsonElement detail, string? cancelBy, bool includeKey = true)
    {
        var body = new Dictionary<string, object?>
        {
            ["name"] = detail.GetProperty("name").GetString(),
            ["category"] = null,
            ["notes"] = null,
            ["currency"] = detail.GetProperty("currency").GetString(),
            ["startDate"] = detail.GetProperty("startDate").GetString(),
            ["endDate"] = null,
            ["catalogId"] = null,
        };
        if (includeKey)
        {
            body["cancelBy"] = cancelBy;
        }

        return body;
    }

    private static async Task<JsonElement> CreateAsync(ApiTestHost host, object body)
    {
        using var response = await host.SendAsync(HttpMethod.Post, Base, body);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        return await ApiTestHost.JsonAsync(response);
    }

    private static Task<JsonElement> PutAsync(ApiTestHost host, JsonElement current, object body) =>
        SubscriptionApiTests.SendOkAsync(host, HttpMethod.Put, $"{Base}/{current.GetProperty("id").GetString()}", body, SubscriptionApiTests.ETag(current));

    private static string? Str(JsonElement e, string name) => e.GetProperty(name).ValueKind == JsonValueKind.Null ? null : e.GetProperty(name).GetString();

    private static DateTimeOffset? Instant(JsonElement e) =>
        e.GetProperty("cancelByInstant").ValueKind == JsonValueKind.Null ? null : e.GetProperty("cancelByInstant").GetDateTimeOffset();

    private static DateTimeOffset Utc(string iso) => DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture);

    private static async Task AssertCancelByRejectedAsync(HttpResponseMessage response)
    {
        await ApiTestHost.AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation-failed");
        var json = await ApiTestHost.JsonAsync(response);
        Assert.True(json.GetProperty("errors").TryGetProperty("cancelBy", out _), json.GetRawText());
    }

    [Fact]
    public async Task Add_with_a_cancel_by_stores_it_and_the_detail_carries_local_time_and_the_utc_instant()
    {
        await using var host = await StartAsync();

        var created = await CreateAsync(host, AddBody(host, "2030-06-15T09:00"));

        Assert.Equal("2030-06-15T09:00", Str(created, "cancelBy"));
        Assert.Equal(Utc("2030-06-15T07:00:00Z"), Instant(created));
        Assert.Equal(TimeSpan.Zero, created.GetProperty("cancelByInstant").GetDateTimeOffset().Offset);
        var detail = await SubscriptionApiTests.GetAsync(host, created.GetProperty("id").GetString()!);
        Assert.Equal("2030-06-15T09:00", Str(detail, "cancelBy"));
        Assert.Equal(Utc("2030-06-15T07:00:00Z"), Instant(detail));
    }

    [Fact]
    public async Task The_overview_summaries_carry_both_fields_and_null_when_there_is_no_deadline()
    {
        await using var host = await StartAsync();
        await CreateAsync(host, AddBody(host, "2030-12-15T09:00"));
        await CreateAsync(host, AddBody(host, null));

        using var response = await host.SendAsync(HttpMethod.Get, Base);
        var overview = await ApiTestHost.JsonAsync(response);

        var items = overview.GetProperty("subscriptions").EnumerateArray().ToList();
        var with = Assert.Single(items, i => Str(i, "cancelBy") is not null);
        Assert.Equal("2030-12-15T09:00", Str(with, "cancelBy"));
        Assert.Equal(Utc("2030-12-15T08:00:00Z"), Instant(with));
        var without = Assert.Single(items, i => Str(i, "cancelBy") is null);
        Assert.Null(Instant(without));
    }

    [Fact]
    public async Task Update_changes_the_cancel_by_and_null_or_an_omitted_key_clears_it()
    {
        await using var host = await StartAsync();
        var created = await CreateAsync(host, AddBody(host, "2030-06-15T09:00"));

        var moved = await PutAsync(host, created, PutBody(created, "2030-06-20T18:45"));
        Assert.Equal("2030-06-20T18:45", Str(moved, "cancelBy"));
        Assert.Equal(Utc("2030-06-20T16:45:00Z"), Instant(moved));
        Assert.Equal(created.GetProperty("version").GetInt64() + 1, moved.GetProperty("version").GetInt64());

        var cleared = await PutAsync(host, moved, PutBody(moved, null));
        Assert.Null(Str(cleared, "cancelBy"));
        Assert.Null(Instant(cleared));

        var again = await PutAsync(host, cleared, PutBody(cleared, "2030-07-01T08:00"));
        Assert.Equal("2030-07-01T08:00", Str(again, "cancelBy"));

        // A body without the key is a full replace: the deadline is cleared, like unsubscribeUrl.
        var omitted = await PutAsync(host, again, PutBody(again, null, includeKey: false));
        Assert.Null(Str(omitted, "cancelBy"));
        Assert.Null(Instant(omitted));
    }

    [Fact]
    public async Task A_cancel_by_before_the_start_date_is_a_400_on_cancel_by_for_add_and_update()
    {
        await using var host = await StartAsync();
        var start = SubscriptionApiTests.Day(host, -100);
        var before = SubscriptionApiTests.Day(host, -101) + "T09:00";

        using var add = await host.SendAsync(HttpMethod.Post, Base, AddBody(host, before, start: start));
        await AssertCancelByRejectedAsync(add);

        var created = await CreateAsync(host, AddBody(host, "2030-06-15T09:00", start: start));
        using var update = await host.SendAsync(HttpMethod.Put, $"{Base}/{created.GetProperty("id").GetString()}", PutBody(created, before), SubscriptionApiTests.ETag(created));
        await AssertCancelByRejectedAsync(update);
    }

    [Fact]
    public async Task A_cancel_by_inside_a_dst_gap_is_a_400_on_cancel_by()
    {
        await using var host = await StartAsync();

        using var gap = await host.SendAsync(HttpMethod.Post, Base, AddBody(host, "2030-03-31T02:30"));

        await AssertCancelByRejectedAsync(gap);
    }

    [Theory]
    [InlineData("2030-06-15T09:00Z")]
    [InlineData("2030-06-15T09:00+02:00")]
    [InlineData("2030-06-15")]
    [InlineData("tomorrow")]
    public async Task A_cancel_by_that_is_not_a_local_date_time_is_a_400_on_cancel_by(string value)
    {
        await using var host = await StartAsync();

        using var response = await host.SendAsync(HttpMethod.Post, Base, AddBody(host, value));

        await AssertCancelByRejectedAsync(response);
    }

    [Fact]
    public async Task An_unresolvable_stored_zone_answers_in_utc_and_logs_one_warning_with_the_household_id_only()
    {
        await using var host = await StartAsync(zone: null);
        var created = await CreateAsync(host, AddBody(host, "2030-06-15T09:00"));
        const string Bogus = "Mars/Olympus-Mons";
        await host.Db.ExecuteAsMigratorAsync($"UPDATE subscribed.mt_doc_household_view SET data = jsonb_set(data, '{{timeZoneId}}', '\"{Bogus}\"') WHERE id = 'household-default'");

        var detail = await SubscriptionApiTests.GetAsync(host, created.GetProperty("id").GetString()!);

        Assert.Equal("2030-06-15T09:00", Str(detail, "cancelBy"));
        Assert.Equal(Utc("2030-06-15T09:00:00Z"), Instant(detail));
        var warnings = host.Logs.Entries.Where(e => e.StartsWith("Warning Subscribed.Household ", StringComparison.Ordinal)).ToList();
        var warning = Assert.Single(warnings);
        Assert.Contains("household-default", warning, StringComparison.Ordinal);
        Assert.Empty(host.Logs.Find(Bogus, "2030-06-15", "StreamCo"));
    }

    [Fact]
    public async Task An_unresolvable_stored_zone_still_accepts_a_cancel_by_as_utc_wall_time()
    {
        await using var host = await StartAsync(zone: null);
        await host.Db.ExecuteAsMigratorAsync("UPDATE subscribed.mt_doc_household_view SET data = jsonb_set(data, '{timeZoneId}', '\"Mars/Olympus-Mons\"') WHERE id = 'household-default'");

        var created = await CreateAsync(host, AddBody(host, "2030-06-15T09:00"));

        Assert.Equal("2030-06-15T09:00", Str(created, "cancelBy"));
        Assert.Equal(Utc("2030-06-15T09:00:00Z"), Instant(created));
    }

    [Fact]
    public async Task A_round_trip_of_the_returned_cancel_by_does_not_move_the_version_or_append_an_event()
    {
        await using var host = await StartAsync(now: Utc("2030-01-10T12:00:00Z"));
        var created = await CreateAsync(host, AddBody(host, "2030-10-27T02:30", start: "2030-01-01"));
        var before = await SubscriptionApiTests.GetAsync(host, created.GetProperty("id").GetString()!);

        var saved = await PutAsync(host, before, PutBody(before, Str(before, "cancelBy")));

        Assert.Equal(before.GetProperty("version").GetInt64(), saved.GetProperty("version").GetInt64());
        Assert.Equal(Str(before, "cancelBy"), Str(saved, "cancelBy"));
        Assert.Equal(Instant(before), Instant(saved));
    }
}
