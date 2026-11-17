using System.Globalization;
using System.Net;
using System.Text.Json;
using Subscribed.TestSupport;
using Xunit;
using static Subscribed.Api.Tests.SubscriptionApiTests;

namespace Subscribed.Api.Tests;

/// <summary>
/// Period totals (#50): the <c>period</c> query parameter, the <c>periods</c> options endpoint and the pause-period endpoints.
/// The host clock cannot be pinned to a past date (the sign-in tokens carry real timestamps and the embedded catalog rejects a
/// date after the clock), so exact figures use explicit calendar years (2024 and 2025) that do not depend on today, and
/// everything that does depend on today (the default year, the rolling windows, the pause buttons) is computed from the host
/// clock in the test. Exact day-fraction arithmetic for every period kind is covered by the unit tests, which take an explicit today.
/// </summary>
public sealed class SubscriptionPeriodApiTests(PostgresFixture pg)
{
    private const string Base = "/api/subscriptions";

    private async Task<ApiTestHost> HostAsync()
    {
        var host = await ApiTestHost.StartAsync(pg);
        await host.InitHouseholdAsync("subject-owner");
        (await host.LoginAsync("subject-owner")).Dispose();
        return host;
    }

    private static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<JsonElement> OverviewForAsync(ApiTestHost host, string? period)
    {
        using var response = await host.SendAsync(HttpMethod.Get, period is null ? Base : $"{Base}?period={period}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiTestHost.JsonAsync(response);
    }

    private static async Task<JsonElement> DetailForAsync(ApiTestHost host, string id, string? period)
    {
        using var response = await host.SendAsync(HttpMethod.Get, period is null ? $"{Base}/{id}" : $"{Base}/{id}?period={period}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ApiTestHost.JsonAsync(response);
    }

    private static string Id(JsonElement detail) => detail.GetProperty("id").GetString()!;

    private static decimal Amount(JsonElement e) => decimal.Parse(e.GetString()!, CultureInfo.InvariantCulture);

    private static Task<JsonElement> AddPauseAsync(ApiTestHost host, JsonElement detail, string pauseDate, string? resumeDate, string query = "") =>
        SendOkAsync(host, HttpMethod.Post, $"{Base}/{Id(detail)}/pauses{query}", new { pauseDate, resumeDate }, ETag(detail));

    // ---- GET ?period= --------------------------------------------------------------------------------

    [Fact]
    public async Task The_default_period_is_the_current_calendar_year_and_equals_the_explicit_year()
    {
        await using var host = await HostAsync();
        var year = Today(host).Year;
        await AddAsync(host, NewBody(host, "StreamCo", amount: "100.00", start: "2020-01-01"));

        var implicitYear = await OverviewForAsync(host, null);
        var explicitYear = await OverviewForAsync(host, $"year-{year}");
        Assert.Equal(implicitYear.GetProperty("period").GetRawText(), explicitYear.GetProperty("period").GetRawText());
        Assert.Equal(implicitYear.GetProperty("totals").GetRawText(), explicitYear.GetProperty("totals").GetRawText());
        var period = implicitYear.GetProperty("period");
        Assert.Equal($"year-{year}", period.GetProperty("key").GetString());
        Assert.Equal($"{year}-01-01", period.GetProperty("from").GetString());
        Assert.Equal($"{year}-12-31", period.GetProperty("to").GetString());
        Assert.Equal(12, period.GetProperty("months").GetInt32());

        // A whole calendar year at a constant price costs exactly its yearly amount, in a leap year too.
        Assert.Equal("1200.00", implicitYear.GetProperty("totals")[0].GetProperty("total").GetString());
        Assert.Equal("100.00", implicitYear.GetProperty("totals")[0].GetProperty("monthlyAverage").GetString());
    }

    [Fact]
    public async Task A_calendar_year_total_follows_the_price_change_day_by_day_and_the_detail_agrees()
    {
        await using var host = await HostAsync();
        var created = await AddAsync(host, NewBody(host, "StreamCo", amount: "100.00", start: "2020-01-01"));
        var priced = await SendOkAsync(host, HttpMethod.Post, $"{Base}/{Id(created)}/prices",
            new { effectiveFrom = "2025-07-01", amount = "200.00", cadenceUnit = "month", cadenceCount = 1 }, ETag(created));

        // 100/month to Jun 30 (181 days), 200/month from Jul 1 (184 days) of 2025: (1200 * 181 + 2400 * 184) / 365 = 1804.93.
        var overview = await OverviewForAsync(host, "year-2025");
        var period = overview.GetProperty("period");
        Assert.Equal(("year-2025", "2025-01-01", "2025-12-31", 12), (period.GetProperty("key").GetString(), period.GetProperty("from").GetString(), period.GetProperty("to").GetString(), period.GetProperty("months").GetInt32()));
        var totals = overview.GetProperty("totals")[0];
        Assert.Equal("1804.93", totals.GetProperty("total").GetString());
        Assert.Equal("150.41", totals.GetProperty("monthlyAverage").GetString());   // 1804.93150... / 12, from the unrounded sum
        Assert.Equal(1, totals.GetProperty("activeCount").GetInt32());

        var row = Row(overview, "StreamCo");
        Assert.Equal("1804.93", row.GetProperty("periodTotal").GetString());
        Assert.True(row.GetProperty("activeInPeriod").GetBoolean());

        var detail = await DetailForAsync(host, Id(priced), "year-2025");
        Assert.Equal("year-2025", detail.GetProperty("period").GetProperty("key").GetString());
        Assert.Equal("1804.93", detail.GetProperty("periodTotal").GetString());

        // Before the first day of the history the subscription costs nothing.
        Assert.Equal("0.00", Row(await OverviewForAsync(host, "year-2019"), "StreamCo").GetProperty("periodTotal").GetString());
    }

    [Fact]
    public async Task The_rolling_windows_follow_the_host_clock_and_the_detail_uses_the_same_window()
    {
        await using var host = await HostAsync();
        var today = Today(host);
        var created = await AddAsync(host, NewBody(host, "StreamCo", amount: "100.00", start: "2020-01-01"));

        var last = (await OverviewForAsync(host, "last-12-months")).GetProperty("period");
        Assert.Equal("last-12-months", last.GetProperty("key").GetString());
        Assert.Equal(Iso(today.AddMonths(-12).AddDays(1)), last.GetProperty("from").GetString());
        Assert.Equal(Iso(today), last.GetProperty("to").GetString());
        Assert.Equal(12, last.GetProperty("months").GetInt32());

        var next = (await OverviewForAsync(host, "next-12-months")).GetProperty("period");
        Assert.Equal("next-12-months", next.GetProperty("key").GetString());
        Assert.Equal(Iso(today), next.GetProperty("from").GetString());
        Assert.Equal(Iso(today.AddMonths(12).AddDays(-1)), next.GetProperty("to").GetString());

        // 100 a month for the whole window is 1200 to the day-fraction rounding of a 365 or 366 day year.
        foreach (var key in new[] { "last-12-months", "next-12-months" })
        {
            var overview = await OverviewForAsync(host, key);
            Assert.InRange(Amount(overview.GetProperty("totals")[0].GetProperty("total")), 1199.00m, 1201.00m);
            Assert.InRange(Amount(overview.GetProperty("totals")[0].GetProperty("monthlyAverage")), 99.90m, 100.10m);

            var detail = await DetailForAsync(host, Id(created), key);
            Assert.Equal(key, detail.GetProperty("period").GetProperty("key").GetString());
            Assert.Equal(Row(overview, "StreamCo").GetProperty("periodTotal").GetString(), detail.GetProperty("periodTotal").GetString());
        }
    }

    [Fact]
    public async Task The_per_month_column_does_not_change_with_the_period()
    {
        await using var host = await HostAsync();
        await AddAsync(host, NewBody(host, "StreamCo", amount: "129.00", start: "2020-01-01"));

        foreach (var key in new[] { null, "year-2025", "year-2019", "last-12-months", "next-12-months" })
        {
            var row = Row(await OverviewForAsync(host, key), "StreamCo");
            Assert.Equal("129.00", row.GetProperty("normalizedMonthly").GetString());
            Assert.Equal("1548.00", row.GetProperty("normalizedYearly").GetString());
        }
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("year-0")]
    [InlineData("year-1899")]
    [InlineData("year-99999")]
    [InlineData("year-26")]
    [InlineData("YEAR-2026")]
    [InlineData("")]
    [InlineData("last-12-months%20")]
    public async Task An_invalid_period_is_400_with_a_period_field_error_on_both_reads(string period)
    {
        await using var host = await HostAsync();
        var created = await AddAsync(host, NewBody(host));

        using var overview = await host.SendAsync(HttpMethod.Get, $"{Base}?period={period}");
        Assert.True((await ErrorsAsync(overview)).TryGetProperty("period", out _));

        using var detail = await host.SendAsync(HttpMethod.Get, $"{Base}/{Id(created)}?period={period}");
        Assert.True((await ErrorsAsync(detail)).TryGetProperty("period", out _));
    }

    [Fact]
    public async Task A_repeated_period_parameter_is_400()
    {
        await using var host = await HostAsync();
        using var response = await host.SendAsync(HttpMethod.Get, $"{Base}?period=year-2026&period=year-2025");
        Assert.True((await ErrorsAsync(response)).TryGetProperty("period", out _));
    }

    [Fact]
    public async Task Removed_subscriptions_are_excluded_and_ended_and_not_started_ones_are_included_when_they_had_chargeable_days()
    {
        await using var host = await HostAsync();
        var gone = await AddAsync(host, NewBody(host, "Gone", amount: "100.00", start: "2020-01-01"));
        await AddAsync(host, NewBody(host, "Ended", amount: "100.00", start: "2024-06-01", end: "2025-03-31"));
        await AddAsync(host, NewBody(host, "Later", amount: "100.00", start: "2025-12-01"));
        await AddAsync(host, NewBody(host, "LongOver", amount: "100.00", start: "2023-01-01", end: "2024-12-31"));
        using (var removed = await host.SendAsync(HttpMethod.Delete, $"{Base}/{Id(gone)}", ifMatch: ETag(gone)))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        var overview = await OverviewForAsync(host, "year-2025");
        Assert.DoesNotContain(overview.GetProperty("subscriptions").EnumerateArray(), r => r.GetProperty("name").GetString() == "Gone");

        var ended = Row(overview, "Ended");
        Assert.Equal("295.89", ended.GetProperty("periodTotal").GetString());   // Jan 1 - Mar 31 = 90 days: 1200 * 90 / 365
        Assert.True(ended.GetProperty("activeInPeriod").GetBoolean());

        var later = Row(overview, "Later");
        Assert.Equal("101.92", later.GetProperty("periodTotal").GetString());   // Dec 1 - Dec 31 = 31 days: 1200 * 31 / 365
        Assert.True(later.GetProperty("activeInPeriod").GetBoolean());

        var over = Row(overview, "LongOver");
        Assert.Equal("0.00", over.GetProperty("periodTotal").GetString());
        Assert.False(over.GetProperty("activeInPeriod").GetBoolean());

        var totals = overview.GetProperty("totals")[0];
        Assert.Equal(2, totals.GetProperty("activeCount").GetInt32());
        Assert.Equal("397.81", totals.GetProperty("total").GetString());   // (1200 * 90 + 1200 * 31) / 365 = 397.8082

        // In 2024 only "Ended" and "LongOver" had chargeable days, and "Later" had none.
        var previous = await OverviewForAsync(host, "year-2024");
        Assert.False(Row(previous, "Later").GetProperty("activeInPeriod").GetBoolean());
        Assert.True(Row(previous, "LongOver").GetProperty("activeInPeriod").GetBoolean());
        Assert.Equal(2, previous.GetProperty("totals")[0].GetProperty("activeCount").GetInt32());
    }

    [Fact]
    public async Task The_totals_keep_one_line_per_currency_with_the_period_total_and_average()
    {
        await using var host = await HostAsync();
        await AddAsync(host, NewBody(host, "Dkk", "DKK", "100.00", start: "2020-01-01"));
        await AddAsync(host, NewBody(host, "Eur", "EUR", "10.00", start: "2020-01-01"));

        var totals = (await OverviewForAsync(host, "year-2025")).GetProperty("totals");
        Assert.Equal(["DKK", "EUR"], totals.EnumerateArray().Select(t => t.GetProperty("currency").GetString()));
        Assert.Equal(("1200.00", "100.00"), (totals[0].GetProperty("total").GetString(), totals[0].GetProperty("monthlyAverage").GetString()));
        Assert.Equal(("120.00", "10.00"), (totals[1].GetProperty("total").GetString(), totals[1].GetProperty("monthlyAverage").GetString()));
    }

    [Fact]
    public async Task The_totals_do_not_contain_the_old_monthly_and_yearly_names()
    {
        await using var host = await HostAsync();
        await AddAsync(host, NewBody(host));

        var line = (await OverviewForAsync(host, null)).GetProperty("totals")[0];
        var names = line.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["activeCount", "currency", "minorUnit", "monthlyAverage", "total"], names);
        Assert.False(line.TryGetProperty("monthly", out _));
        Assert.False(line.TryGetProperty("yearly", out _));
    }

    // ---- GET periods and routing ---------------------------------------------------------------------

    [Fact]
    public async Task The_periods_endpoint_offers_the_years_from_the_earliest_start_to_next_year()
    {
        await using var host = await HostAsync();
        var year = Today(host).Year;
        using (var empty = await host.SendAsync(HttpMethod.Get, Base + "/periods"))
        {
            Assert.Equal(HttpStatusCode.OK, empty.StatusCode);
            Assert.Equal("no-store", empty.Headers.CacheControl?.ToString());
            var none = await ApiTestHost.JsonAsync(empty);
            Assert.Equal([year, year + 1], none.GetProperty("years").EnumerateArray().Select(y => y.GetInt32()));
            Assert.Equal(year, none.GetProperty("currentYear").GetInt32());
        }

        var gone = await AddAsync(host, NewBody(host, "Gone", start: "2019-03-01"));
        await AddAsync(host, NewBody(host, "Old", start: "2023-05-01"));
        using (var removed = await host.SendAsync(HttpMethod.Delete, $"{Base}/{Id(gone)}", ifMatch: ETag(gone)))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        using var response = await host.SendAsync(HttpMethod.Get, Base + "/periods");
        var options = await ApiTestHost.JsonAsync(response);
        Assert.Equal(Enumerable.Range(2023, year + 1 - 2023 + 1), options.GetProperty("years").EnumerateArray().Select(y => y.GetInt32()));
    }

    [Fact]
    public async Task Periods_is_not_captured_by_the_id_route_and_pause_and_pauses_both_route()
    {
        await using var host = await HostAsync();
        var created = await AddAsync(host, NewBody(host, start: "2020-01-01"));

        // If {id} captured "periods" this would be a 404 for a malformed id.
        using (var periods = await host.SendAsync(HttpMethod.Get, Base + "/periods"))
        {
            Assert.Equal(HttpStatusCode.OK, periods.StatusCode);
            Assert.True((await ApiTestHost.JsonAsync(periods)).TryGetProperty("years", out _));
        }

        // The id route still answers for a real id, and a literal "pauses" segment does not collide with "pause".
        Assert.Equal(Id(created), (await DetailForAsync(host, Id(created), null)).GetProperty("id").GetString());
        var withPeriod = await AddPauseAsync(host, created, "2025-02-01", "2025-02-10");
        Assert.Single(withPeriod.GetProperty("pauseHistory").EnumerateArray());
        var paused = await SendOkAsync(host, HttpMethod.Post, $"{Base}/{Id(created)}/pause", null, ETag(withPeriod));
        Assert.True(paused.GetProperty("paused").GetBoolean());
        Assert.Equal(2, paused.GetProperty("pauseHistory").GetArrayLength());
    }

    // ---- pause endpoints -----------------------------------------------------------------------------

    [Fact]
    public async Task Adding_a_pause_returns_200_with_the_new_etag_the_history_and_a_reduced_period_total()
    {
        await using var host = await HostAsync();
        var created = await AddAsync(host, NewBody(host, "StreamCo", amount: "100.00", start: "2020-01-01"));
        Assert.Equal("1200.00", created.GetProperty("periodTotal").GetString());
        Assert.Empty(created.GetProperty("pauseHistory").EnumerateArray());

        using var response = await host.SendAsync(HttpMethod.Post, $"{Base}/{Id(created)}/pauses?period=year-2025", new { pauseDate = "2025-03-01", resumeDate = "2025-04-01" }, ETag(created));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await ApiTestHost.JsonAsync(response);
        Assert.Equal(created.GetProperty("version").GetInt64() + 1, detail.GetProperty("version").GetInt64());
        Assert.Equal(ETag(detail), response.Headers.ETag?.ToString());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.False(detail.GetProperty("paused").GetBoolean());

        var history = detail.GetProperty("pauseHistory");
        Assert.Equal(1, history.GetArrayLength());
        Assert.Matches("^[0-9a-f]{32}$", history[0].GetProperty("id").GetString());
        Assert.Equal("2025-03-01", history[0].GetProperty("pauseDate").GetString());
        Assert.Equal("2025-04-01", history[0].GetProperty("resumeDate").GetString());

        // 31 paused days of 365: 1200 * 334 / 365 = 1098.08, in the detail and in the overview.
        Assert.Equal("year-2025", detail.GetProperty("period").GetProperty("key").GetString());
        Assert.Equal("1098.08", detail.GetProperty("periodTotal").GetString());
        var overview = await OverviewForAsync(host, "year-2025");
        Assert.Equal("1098.08", Row(overview, "StreamCo").GetProperty("periodTotal").GetString());
        Assert.Equal("1098.08", overview.GetProperty("totals")[0].GetProperty("total").GetString());

        // The pause is outside the current year, so the default year is untouched.
        Assert.Equal("1200.00", (await DetailForAsync(host, Id(created), null)).GetProperty("periodTotal").GetString());
    }

    [Fact]
    public async Task An_open_pause_has_no_resume_date_and_a_change_closes_it()
    {
        await using var host = await HostAsync();
        var today = Today(host);
        var created = await AddAsync(host, NewBody(host, amount: "100.00", start: "2020-01-01"));

        var opened = await AddPauseAsync(host, created, Iso(today.AddDays(-30)), null);
        var periodId = opened.GetProperty("pauseHistory")[0].GetProperty("id").GetString()!;
        Assert.Equal(JsonValueKind.Null, opened.GetProperty("pauseHistory")[0].GetProperty("resumeDate").ValueKind);
        Assert.True(opened.GetProperty("paused").GetBoolean());
        Assert.Equal("paused", opened.GetProperty("status").GetString());

        var closed = await SendOkAsync(host, HttpMethod.Put, $"{Base}/{Id(created)}/pauses/{periodId}", new { pauseDate = Iso(today.AddDays(-30)), resumeDate = Iso(today.AddDays(-20)) }, ETag(opened));
        Assert.Equal(Iso(today.AddDays(-20)), closed.GetProperty("pauseHistory")[0].GetProperty("resumeDate").GetString());
        Assert.Equal(periodId, closed.GetProperty("pauseHistory")[0].GetProperty("id").GetString());
        Assert.False(closed.GetProperty("paused").GetBoolean());
        Assert.Equal("active", closed.GetProperty("status").GetString());
        Assert.Equal(opened.GetProperty("version").GetInt64() + 1, closed.GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task Deleting_a_pause_returns_the_detail_without_it_and_restores_the_total()
    {
        await using var host = await HostAsync();
        var created = await AddAsync(host, NewBody(host, amount: "100.00", start: "2020-01-01"));
        var withPause = await AddPauseAsync(host, created, "2025-03-01", "2025-04-01");
        var periodId = withPause.GetProperty("pauseHistory")[0].GetProperty("id").GetString()!;
        Assert.Equal("1098.08", (await DetailForAsync(host, Id(created), "year-2025")).GetProperty("periodTotal").GetString());

        using var response = await host.SendAsync(HttpMethod.Delete, $"{Base}/{Id(created)}/pauses/{periodId}?period=year-2025", ifMatch: ETag(withPause));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await ApiTestHost.JsonAsync(response);
        Assert.Equal(ETag(detail), response.Headers.ETag?.ToString());
        Assert.Empty(detail.GetProperty("pauseHistory").EnumerateArray());
        Assert.Equal("1200.00", detail.GetProperty("periodTotal").GetString());

        // Deleting it again is a 404: the period no longer exists.
        using var again = await host.SendAsync(HttpMethod.Delete, $"{Base}/{Id(created)}/pauses/{periodId}", ifMatch: ETag(detail));
        await ApiTestHost.AssertProblemAsync(again, HttpStatusCode.NotFound, "not-found");
    }

    [Fact]
    public async Task The_pause_history_is_chronological_and_is_not_filtered_by_the_requested_period()
    {
        await using var host = await HostAsync();
        var created = await AddAsync(host, NewBody(host, amount: "100.00", start: "2020-01-01"));
        var later = await AddPauseAsync(host, created, "2025-05-01", "2025-05-05");
        var earlier = await AddPauseAsync(host, later, "2025-01-01", "2025-01-03");

        foreach (var key in new[] { null, "year-2024", "year-2025", "next-12-months" })
        {
            var detail = await DetailForAsync(host, Id(created), key);
            Assert.Equal(["2025-01-01", "2025-05-01"], detail.GetProperty("pauseHistory").EnumerateArray().Select(p => p.GetProperty("pauseDate").GetString()));
        }

        Assert.Equal(2, earlier.GetProperty("pauseHistory").GetArrayLength());
    }

    [Fact]
    public async Task Pause_period_requests_without_if_match_are_428_malformed_is_400_and_stale_is_412()
    {
        await using var host = await HostAsync();
        var created = await AddAsync(host, NewBody(host, start: "2020-01-01"));
        var id = Id(created);
        var withPause = await AddPauseAsync(host, created, "2025-03-01", "2025-04-01");
        var periodId = withPause.GetProperty("pauseHistory")[0].GetProperty("id").GetString()!;
        var body = new { pauseDate = "2025-05-01", resumeDate = "2025-05-02" };

        var routes = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Post, $"{Base}/{id}/pauses", body),
            (HttpMethod.Put, $"{Base}/{id}/pauses/{periodId}", body),
            (HttpMethod.Delete, $"{Base}/{id}/pauses/{periodId}", null),
        };

        foreach (var (method, path, payload) in routes)
        {
            using var missing = await host.SendAsync(method, path, payload);
            await ApiTestHost.AssertProblemAsync(missing, HttpStatusCode.PreconditionRequired, "if-match-required");

            using var malformed = await host.SendAsync(method, path, payload, "3");
            Assert.True((await ErrorsAsync(malformed)).TryGetProperty("ifMatch", out _));

            using var stale = await host.SendAsync(method, path, payload, ETag(created));
            await ApiTestHost.AssertProblemAsync(stale, HttpStatusCode.PreconditionFailed, "concurrency-conflict");
            Assert.Equal(withPause.GetProperty("version").GetInt64(), (await ApiTestHost.JsonAsync(stale)).GetProperty("currentVersion").GetInt64());
        }

        Assert.Equal(withPause.GetProperty("version").GetInt64(), (await GetAsync(host, id)).GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task Pause_period_requests_for_an_unknown_subscription_or_period_are_404()
    {
        await using var host = await HostAsync();
        var created = await AddAsync(host, NewBody(host, start: "2020-01-01"));
        var id = Id(created);
        var withPause = await AddPauseAsync(host, created, "2025-03-01", "2025-04-01");
        var body = new { pauseDate = "2025-05-01", resumeDate = "2025-05-02" };
        var current = ETag(withPause);

        // An unknown or malformed subscription id, and period ids that are unknown or malformed.
        var unknownSubscription = new string('0', 32);
        foreach (var (method, path, payload) in new (HttpMethod, string, object?)[]
        {
            (HttpMethod.Post, $"{Base}/{unknownSubscription}/pauses", body),
            (HttpMethod.Post, $"{Base}/NOT-AN-ID/pauses", body),
            (HttpMethod.Put, $"{Base}/{unknownSubscription}/pauses/{new string('a', 32)}", body),
            (HttpMethod.Put, $"{Base}/{id}/pauses/{new string('a', 32)}", body),
            (HttpMethod.Put, $"{Base}/{id}/pauses/legacy-1", body),
            (HttpMethod.Put, $"{Base}/{id}/pauses/legacy-0", body),
            (HttpMethod.Put, $"{Base}/{id}/pauses/NOT-A-PERIOD", body),
            (HttpMethod.Put, $"{Base}/{id}/pauses/{new string('A', 32)}", body),
            (HttpMethod.Put, $"{Base}/{id}/pauses/legacy-12345", body),
            (HttpMethod.Delete, $"{Base}/{id}/pauses/{new string('a', 32)}", null),
            (HttpMethod.Delete, $"{Base}/{id}/pauses/legacy-1", null),
            (HttpMethod.Delete, $"{Base}/{id}/pauses/NOT-A-PERIOD", null),
        })
        {
            using var response = await host.SendAsync(method, path, payload, current);
            await ApiTestHost.AssertProblemAsync(response, HttpStatusCode.NotFound, "not-found");
        }

        Assert.Equal(withPause.GetProperty("version").GetInt64(), (await GetAsync(host, id)).GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task A_removed_subscription_is_404_for_the_pause_period_endpoints_even_with_a_stale_if_match()
    {
        await using var host = await HostAsync();
        var created = await AddAsync(host, NewBody(host, start: "2020-01-01"));
        var id = Id(created);
        using (var removed = await host.SendAsync(HttpMethod.Delete, $"{Base}/{id}", ifMatch: ETag(created)))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        using var response = await host.SendAsync(HttpMethod.Post, $"{Base}/{id}/pauses", new { pauseDate = "2025-03-01" }, "\"1\"");
        await ApiTestHost.AssertProblemAsync(response, HttpStatusCode.NotFound, "not-found");
    }

    // "d+1" and friends are days relative to the host clock, resolved in the test.
    [Theory]
    [InlineData(null, null, "pauseDate")]
    [InlineData("", null, "pauseDate")]
    [InlineData("01/03/2025", null, "pauseDate")]
    [InlineData("2025-02-30", null, "pauseDate")]
    [InlineData("2025-03-01", "tomorrow", "resumeDate")]
    [InlineData("d+1", null, "pauseDate")]          // after today (R5.7)
    [InlineData("d-5", "d+1", "resumeDate")]        // after today (R5.7)
    [InlineData("d-5", "d-5", "resumeDate")]        // not after the pause date (R5.5)
    [InlineData("d-4", "d-5", "resumeDate")]
    public async Task Invalid_pause_dates_are_400_with_the_field_error_and_store_nothing(string? pauseDate, string? resumeDate, string field)
    {
        await using var host = await HostAsync();
        var created = await AddAsync(host, NewBody(host, start: "2020-01-01"));
        string? Resolve(string? value) => value is ['d', ..] ? Iso(Today(host).AddDays(int.Parse(value[1..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture))) : value;

        using var response = await host.SendAsync(HttpMethod.Post, $"{Base}/{Id(created)}/pauses", new { pauseDate = Resolve(pauseDate), resumeDate = Resolve(resumeDate) }, ETag(created));
        Assert.True((await ErrorsAsync(response)).TryGetProperty(field, out _));
        Assert.Equal(created.GetProperty("version").GetInt64(), (await GetAsync(host, Id(created))).GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task Overlapping_and_second_open_pauses_are_400_but_adjacent_pauses_are_allowed()
    {
        await using var host = await HostAsync();
        var created = await AddAsync(host, NewBody(host, start: "2020-01-01"));
        var first = await AddPauseAsync(host, created, "2025-03-01", "2025-03-10");

        using (var overlap = await host.SendAsync(HttpMethod.Post, $"{Base}/{Id(created)}/pauses", new { pauseDate = "2025-03-09", resumeDate = "2025-03-20" }, ETag(first)))
        {
            Assert.True((await ErrorsAsync(overlap)).TryGetProperty("pauseDate", out _));
        }

        // Adjacent: this one pauses on the day the first resumes.
        var second = await AddPauseAsync(host, first, "2025-03-10", "2025-03-12");
        Assert.Equal(2, second.GetProperty("pauseHistory").GetArrayLength());

        var open = await AddPauseAsync(host, second, "2025-05-01", null);
        using (var secondOpen = await host.SendAsync(HttpMethod.Post, $"{Base}/{Id(created)}/pauses", new { pauseDate = "2025-06-01", resumeDate = (string?)null }, ETag(open)))
        {
            Assert.True((await ErrorsAsync(secondOpen)).EnumerateObject().Any());
        }

        // An open period before a later one: only the latest pause can be open.
        using (var before = await host.SendAsync(HttpMethod.Post, $"{Base}/{Id(created)}/pauses", new { pauseDate = "2025-04-01", resumeDate = (string?)null }, ETag(open)))
        {
            Assert.True((await ErrorsAsync(before)).TryGetProperty("resumeDate", out _));
        }
    }

    [Fact]
    public async Task Changing_a_period_ignores_itself_in_the_overlap_check()
    {
        await using var host = await HostAsync();
        var created = await AddAsync(host, NewBody(host, start: "2020-01-01"));
        var withPause = await AddPauseAsync(host, created, "2025-03-01", "2025-03-10");
        var periodId = withPause.GetProperty("pauseHistory")[0].GetProperty("id").GetString()!;

        var changed = await SendOkAsync(host, HttpMethod.Put, $"{Base}/{Id(created)}/pauses/{periodId}", new { pauseDate = "2025-03-05", resumeDate = "2025-03-15" }, ETag(withPause));
        Assert.Equal("2025-03-05", changed.GetProperty("pauseHistory")[0].GetProperty("pauseDate").GetString());
        Assert.Equal("2025-03-15", changed.GetProperty("pauseHistory")[0].GetProperty("resumeDate").GetString());
    }

    [Fact]
    public async Task Pause_periods_may_lie_outside_the_subscription_dates_and_change_nothing_there()
    {
        await using var host = await HostAsync();
        var created = await AddAsync(host, NewBody(host, "Window", amount: "100.00", start: "2025-02-01", end: "2025-03-31"));
        var before = (await DetailForAsync(host, Id(created), "year-2025")).GetProperty("periodTotal").GetString();
        Assert.Equal("193.97", before);   // Feb 1 - Mar 31 = 59 days: 1200 * 59 / 365

        var early = await AddPauseAsync(host, created, "2024-06-01", "2024-07-01", "?period=year-2025");
        Assert.Equal(before, early.GetProperty("periodTotal").GetString());
        var late = await AddPauseAsync(host, early, "2025-05-01", "2025-05-10", "?period=year-2025");
        Assert.Equal(before, late.GetProperty("periodTotal").GetString());
        Assert.Equal(2, late.GetProperty("pauseHistory").GetArrayLength());
    }

    [Fact]
    public async Task A_mutation_with_an_unparseable_period_succeeds_and_returns_the_current_year()
    {
        await using var host = await HostAsync();
        var currentKey = $"year-{Today(host).Year}";
        var created = await AddAsync(host, NewBody(host, amount: "100.00", start: "2020-01-01"));
        var id = Id(created);

        // D1: the write has happened, so it is not reported as failed; the detail falls back to the current year.
        using var add = await host.SendAsync(HttpMethod.Post, $"{Base}/{id}/pauses?period=garbage", new { pauseDate = "2025-03-01", resumeDate = "2025-04-01" }, ETag(created));
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        var added = await ApiTestHost.JsonAsync(add);
        Assert.Equal(currentKey, added.GetProperty("period").GetProperty("key").GetString());
        var periodId = added.GetProperty("pauseHistory")[0].GetProperty("id").GetString()!;

        using var change = await host.SendAsync(HttpMethod.Put, $"{Base}/{id}/pauses/{periodId}?period=garbage", new { pauseDate = "2025-03-01", resumeDate = "2025-03-11" }, ETag(added));
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        var changed = await ApiTestHost.JsonAsync(change);
        Assert.Equal(currentKey, changed.GetProperty("period").GetProperty("key").GetString());
        Assert.Equal("2025-03-11", changed.GetProperty("pauseHistory")[0].GetProperty("resumeDate").GetString());

        using var pause = await host.SendAsync(HttpMethod.Post, $"{Base}/{id}/pause?period=garbage", ifMatch: ETag(changed));
        Assert.Equal(HttpStatusCode.OK, pause.StatusCode);
        Assert.Equal(currentKey, (await ApiTestHost.JsonAsync(pause)).GetProperty("period").GetProperty("key").GetString());

        // The writes were persisted, and a read of the same value is still a 400.
        Assert.Equal(created.GetProperty("version").GetInt64() + 3, (await GetAsync(host, id)).GetProperty("version").GetInt64());
        using var read = await host.SendAsync(HttpMethod.Get, $"{Base}/{id}?period=garbage");
        Assert.True((await ErrorsAsync(read)).TryGetProperty("period", out _));
    }

    [Fact]
    public async Task A_mutation_honours_a_valid_period()
    {
        await using var host = await HostAsync();
        var created = await AddAsync(host, NewBody(host, amount: "100.00", start: "2020-01-01"));

        using var add = await host.SendAsync(HttpMethod.Post, $"{Base}/{Id(created)}/pauses?period=last-12-months", new { pauseDate = "2025-03-01", resumeDate = "2025-04-01" }, ETag(created));
        Assert.Equal(HttpStatusCode.OK, add.StatusCode);
        var detail = await ApiTestHost.JsonAsync(add);
        Assert.Equal("last-12-months", detail.GetProperty("period").GetProperty("key").GetString());
    }

    [Fact]
    public async Task The_pause_endpoints_need_the_csrf_header_and_a_matching_origin()
    {
        await using var host = await HostAsync();
        var created = await AddAsync(host, NewBody(host, start: "2020-01-01"));
        var body = new { pauseDate = "2025-03-01", resumeDate = "2025-03-05" };

        using var noCsrf = await host.SendAsync(HttpMethod.Post, $"{Base}/{Id(created)}/pauses", body, ETag(created), csrf: false);
        await ApiTestHost.AssertProblemAsync(noCsrf, HttpStatusCode.Forbidden, "csrf-header-required");

        using var foreign = await host.SendAsync(HttpMethod.Post, $"{Base}/{Id(created)}/pauses", body, ETag(created), origin: "https://evil.example");
        await ApiTestHost.AssertProblemAsync(foreign, HttpStatusCode.Forbidden, "origin-mismatch");

        Assert.Equal(created.GetProperty("version").GetInt64(), (await GetAsync(host, Id(created))).GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task The_pause_and_resume_buttons_keep_their_409_behaviour_on_the_new_model()
    {
        await using var host = await HostAsync();
        var today = Today(host);
        var created = await AddAsync(host, NewBody(host, start: "2020-01-01"));
        var id = Id(created);

        var paused = await SendOkAsync(host, HttpMethod.Post, $"{Base}/{id}/pause", null, ETag(created));
        Assert.Equal(Iso(today), paused.GetProperty("pauseHistory")[0].GetProperty("pauseDate").GetString());
        using (var again = await host.SendAsync(HttpMethod.Post, $"{Base}/{id}/pause", ifMatch: ETag(paused)))
        {
            await ApiTestHost.AssertProblemAsync(again, HttpStatusCode.Conflict, "invalid-subscription-state");
        }

        // Resume on the pause day removes the period (D6).
        var resumed = await SendOkAsync(host, HttpMethod.Post, $"{Base}/{id}/resume", null, ETag(paused));
        Assert.Empty(resumed.GetProperty("pauseHistory").EnumerateArray());
        using (var notPaused = await host.SendAsync(HttpMethod.Post, $"{Base}/{id}/resume", ifMatch: ETag(resumed)))
        {
            await ApiTestHost.AssertProblemAsync(notPaused, HttpStatusCode.Conflict, "invalid-subscription-state");
        }

        // A day later Resume closes the open period instead.
        var pausedAgain = await SendOkAsync(host, HttpMethod.Post, $"{Base}/{id}/pause", null, ETag(resumed));
        host.Time.Advance(TimeSpan.FromDays(2));
        (await host.LoginAsync("subject-owner")).Dispose();   // the session idles out after two hours
        var closed = await SendOkAsync(host, HttpMethod.Post, $"{Base}/{id}/resume", null, ETag(pausedAgain));
        Assert.Equal(Iso(today.AddDays(2)), closed.GetProperty("pauseHistory")[0].GetProperty("resumeDate").GetString());
        Assert.False(closed.GetProperty("paused").GetBoolean());
    }
}
