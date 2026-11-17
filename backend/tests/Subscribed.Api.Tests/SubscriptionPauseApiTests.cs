using System.Net;
using System.Text.Json;
using Subscribed.TestSupport;
using Xunit;
using static Subscribed.Api.Tests.SubscriptionApiTests;

namespace Subscribed.Api.Tests;

/// <summary>Pause and resume (#19): <c>POST {id}/pause</c> and <c>POST {id}/resume</c> on the If-Match pipeline.</summary>
public sealed class SubscriptionPauseApiTests(PostgresFixture pg)
{
    private const string Base = "/api/subscriptions";

    private async Task<ApiTestHost> MemberHostAsync()
    {
        var host = await ApiTestHost.StartAsync(pg);
        await host.InitHouseholdAsync("subject-owner");
        (await host.LoginAsync("subject-owner")).Dispose();
        return host;
    }

    private static Task<JsonElement> PauseAsync(ApiTestHost host, JsonElement detail) =>
        SendOkAsync(host, HttpMethod.Post, $"{Base}/{detail.GetProperty("id").GetString()}/pause", null, ETag(detail));

    private static Task<JsonElement> ResumeAsync(ApiTestHost host, JsonElement detail) =>
        SendOkAsync(host, HttpMethod.Post, $"{Base}/{detail.GetProperty("id").GetString()}/resume", null, ETag(detail));

    private static async Task<long> AssertInvalidStateAsync(HttpResponseMessage response)
    {
        await ApiTestHost.AssertProblemAsync(response, HttpStatusCode.Conflict, "invalid-subscription-state");
        return (await ApiTestHost.JsonAsync(response)).GetProperty("currentVersion").GetInt64();
    }

    // ---- check order: stale version (412) is decided before the state rule (409) ---------------------

    [Fact]
    public async Task A_stale_if_match_on_an_already_paused_subscription_is_412_not_409()
    {
        await using var host = await MemberHostAsync();
        var created = await AddAsync(host, NewBody(host));
        var id = created.GetProperty("id").GetString()!;
        var paused = await PauseAsync(host, created);

        using var stale = await host.SendAsync(HttpMethod.Post, $"{Base}/{id}/pause", ifMatch: ETag(created));
        await ApiTestHost.AssertProblemAsync(stale, HttpStatusCode.PreconditionFailed, "concurrency-conflict");
        Assert.Equal(paused.GetProperty("version").GetInt64(), (await ApiTestHost.JsonAsync(stale)).GetProperty("currentVersion").GetInt64());
    }

    [Fact]
    public async Task A_stale_if_match_on_resume_of_a_subscription_that_is_not_paused_is_412_not_409()
    {
        await using var host = await MemberHostAsync();
        var created = await AddAsync(host, NewBody(host));
        var id = created.GetProperty("id").GetString()!;
        var renamed = await SendOkAsync(host, HttpMethod.Put, $"{Base}/{id}", Put(created, name: "Renamed"), ETag(created));

        using var stale = await host.SendAsync(HttpMethod.Post, $"{Base}/{id}/resume", ifMatch: ETag(created));
        await ApiTestHost.AssertProblemAsync(stale, HttpStatusCode.PreconditionFailed, "concurrency-conflict");

        using var current = await host.SendAsync(HttpMethod.Post, $"{Base}/{id}/resume", ifMatch: ETag(renamed));
        await AssertInvalidStateAsync(current);
    }

    [Fact]
    public async Task A_current_if_match_on_an_already_paused_subscription_is_409()
    {
        await using var host = await MemberHostAsync();
        var created = await AddAsync(host, NewBody(host));
        var paused = await PauseAsync(host, created);

        using var again = await host.SendAsync(HttpMethod.Post, $"{Base}/{paused.GetProperty("id").GetString()}/pause", ifMatch: ETag(paused));
        Assert.Equal(paused.GetProperty("version").GetInt64(), await AssertInvalidStateAsync(again));
        Assert.Equal(paused.GetProperty("version").GetInt64(), (await GetAsync(host, paused.GetProperty("id").GetString()!)).GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task A_missing_if_match_on_a_removed_subscription_follows_the_documented_order()
    {
        await using var host = await MemberHostAsync();
        var created = await AddAsync(host, NewBody(host));
        var id = created.GetProperty("id").GetString()!;
        using (var removed = await host.SendAsync(HttpMethod.Delete, $"{Base}/{id}", ifMatch: ETag(created)))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        foreach (var action in new[] { "pause", "resume" })
        {
            // The If-Match check (428) comes before the load (404); with a header, the removed subscription is 404 even if stale.
            using var missing = await host.SendAsync(HttpMethod.Post, $"{Base}/{id}/{action}");
            await ApiTestHost.AssertProblemAsync(missing, HttpStatusCode.PreconditionRequired, "if-match-required");

            using var stale = await host.SendAsync(HttpMethod.Post, $"{Base}/{id}/{action}", ifMatch: "\"1\"");
            await ApiTestHost.AssertProblemAsync(stale, HttpStatusCode.NotFound, "not-found");
        }
    }

    // ---- behaviour ------------------------------------------------------------------------------------

    [Fact]
    public async Task Pause_returns_200_with_the_paused_status_flag_and_a_new_etag_and_resume_restores_active()
    {
        await using var host = await MemberHostAsync();
        var created = await AddAsync(host, NewBody(host));
        Assert.False(created.GetProperty("paused").GetBoolean());
        var id = created.GetProperty("id").GetString()!;

        using var response = await host.SendAsync(HttpMethod.Post, $"{Base}/{id}/pause", ifMatch: ETag(created));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paused = await ApiTestHost.JsonAsync(response);
        Assert.Equal("paused", paused.GetProperty("status").GetString());
        Assert.True(paused.GetProperty("paused").GetBoolean());
        Assert.Equal(created.GetProperty("version").GetInt64() + 1, paused.GetProperty("version").GetInt64());
        Assert.Equal(ETag(paused), response.Headers.ETag?.ToString());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("paused", (await GetAsync(host, id)).GetProperty("status").GetString());

        var resumed = await ResumeAsync(host, paused);
        Assert.Equal("active", resumed.GetProperty("status").GetString());
        Assert.False(resumed.GetProperty("paused").GetBoolean());
        Assert.Equal(paused.GetProperty("version").GetInt64() + 1, resumed.GetProperty("version").GetInt64());

        var again = await PauseAsync(host, resumed);
        Assert.Equal("paused", again.GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_overview_reports_the_paused_row_with_its_version_and_charges_a_paused_subscription_only_for_the_days_before_the_pause()
    {
        // The real clock is used here, so the totals are asserted as a range: wi2 pins the clock and asserts exact period totals.
        await using var host = await MemberHostAsync();
        var keep = await AddAsync(host, NewBody(host, "Keep", amount: "100.00"));
        var pausable = await AddAsync(host, NewBody(host, "Pausable", amount: "50.00"));
        var before = await OverviewAsync(host);
        var beforeTotal = before.GetProperty("totals")[0].GetProperty("total").GetString()!;
        Assert.Equal(2, before.GetProperty("totals")[0].GetProperty("activeCount").GetInt32());

        var paused = await PauseAsync(host, pausable);
        var overview = await OverviewAsync(host);
        var rows = overview.GetProperty("subscriptions").EnumerateArray().ToDictionary(r => r.GetProperty("name").GetString()!);
        Assert.Equal("paused", rows["Pausable"].GetProperty("status").GetString());
        Assert.True(rows["Pausable"].GetProperty("paused").GetBoolean());
        Assert.Equal(paused.GetProperty("version").GetInt64(), rows["Pausable"].GetProperty("version").GetInt64());
        Assert.False(rows["Keep"].GetProperty("paused").GetBoolean());
        Assert.Equal(keep.GetProperty("version").GetInt64(), rows["Keep"].GetProperty("version").GetInt64());
        var totals = overview.GetProperty("totals")[0];
        var total = decimal.Parse(totals.GetProperty("total").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(total <= decimal.Parse(beforeTotal, System.Globalization.CultureInfo.InvariantCulture));

        // Resume on the pause day removes the period, so the same days are charged again.
        await ResumeAsync(host, paused);
        Assert.Equal(beforeTotal, (await OverviewAsync(host)).GetProperty("totals")[0].GetProperty("total").GetString());
    }

    [Fact]
    public async Task Dates_win_over_the_pause_flag_but_the_flag_stays_reachable()
    {
        await using var host = await MemberHostAsync();
        var future = await AddAsync(host, NewBody(host, "Later", start: Day(host, 20)));
        var pausedFuture = await PauseAsync(host, future);
        Assert.Equal("not-started", pausedFuture.GetProperty("status").GetString());
        Assert.True(pausedFuture.GetProperty("paused").GetBoolean());
        Assert.Equal("not-started", (await ResumeAsync(host, pausedFuture)).GetProperty("status").GetString());

        var ended = await AddAsync(host, NewBody(host, "Over", start: Day(host, -100), end: Day(host, -10)));
        var pausedEnded = await PauseAsync(host, ended);
        Assert.Equal("ended", pausedEnded.GetProperty("status").GetString());
        Assert.True(pausedEnded.GetProperty("paused").GetBoolean());

        var reinstated = await SendOkAsync(host, HttpMethod.Put, $"{Base}/{ended.GetProperty("id").GetString()}", Put(pausedEnded, keepEnd: false), ETag(pausedEnded));
        Assert.Equal("paused", reinstated.GetProperty("status").GetString());
        Assert.Equal("active", (await ResumeAsync(host, reinstated)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Pausing_leaves_the_price_history_untouched()
    {
        await using var host = await MemberHostAsync();
        var created = await AddAsync(host, NewBody(host, amount: "59.00", start: Day(host, -300)));
        var paused = await PauseAsync(host, created);
        Assert.Equal(created.GetProperty("priceHistory").GetRawText(), paused.GetProperty("priceHistory").GetRawText());
        Assert.Equal(created.GetProperty("currentPrice").GetRawText(), paused.GetProperty("currentPrice").GetRawText());
    }

    [Fact]
    public async Task Pause_and_resume_without_if_match_are_428_and_malformed_if_match_is_400()
    {
        await using var host = await MemberHostAsync();
        var created = await AddAsync(host, NewBody(host));
        var id = created.GetProperty("id").GetString()!;
        var paused = await PauseAsync(host, created);

        foreach (var action in new[] { "pause", "resume" })
        {
            using var missing = await host.SendAsync(HttpMethod.Post, $"{Base}/{id}/{action}");
            await ApiTestHost.AssertProblemAsync(missing, HttpStatusCode.PreconditionRequired, "if-match-required");

            using var malformed = await host.SendAsync(HttpMethod.Post, $"{Base}/{id}/{action}", ifMatch: "3");
            Assert.True((await ErrorsAsync(malformed)).TryGetProperty("ifMatch", out _));
        }

        Assert.Equal(paused.GetProperty("version").GetInt64(), (await GetAsync(host, id)).GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task Resume_when_not_paused_and_a_double_pause_are_409_with_the_current_version()
    {
        await using var host = await MemberHostAsync();
        var created = await AddAsync(host, NewBody(host));
        var id = created.GetProperty("id").GetString()!;

        using (var resume = await host.SendAsync(HttpMethod.Post, $"{Base}/{id}/resume", ifMatch: ETag(created)))
        {
            Assert.Equal(created.GetProperty("version").GetInt64(), await AssertInvalidStateAsync(resume));
        }

        var paused = await PauseAsync(host, created);
        using (var pause = await host.SendAsync(HttpMethod.Post, $"{Base}/{id}/pause", ifMatch: ETag(paused)))
        {
            Assert.Equal(paused.GetProperty("version").GetInt64(), await AssertInvalidStateAsync(pause));
        }

        var resumed = await ResumeAsync(host, paused);
        using (var resumeAgain = await host.SendAsync(HttpMethod.Post, $"{Base}/{id}/resume", ifMatch: ETag(resumed)))
        {
            Assert.Equal(resumed.GetProperty("version").GetInt64(), await AssertInvalidStateAsync(resumeAgain));
        }
    }

    [Fact]
    public async Task Two_pauses_with_the_same_if_match_give_one_200_and_one_412()
    {
        await using var host = await MemberHostAsync();
        var created = await AddAsync(host, NewBody(host));
        var id = created.GetProperty("id").GetString()!;

        using var first = await host.SendAsync(HttpMethod.Post, $"{Base}/{id}/pause", ifMatch: ETag(created));
        using var second = await host.SendAsync(HttpMethod.Post, $"{Base}/{id}/pause", ifMatch: ETag(created));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await ApiTestHost.AssertProblemAsync(second, HttpStatusCode.PreconditionFailed, "concurrency-conflict");
        Assert.True((await GetAsync(host, id)).GetProperty("paused").GetBoolean());
    }

    [Fact]
    public async Task Concurrent_pauses_at_the_same_version_never_both_succeed()
    {
        await using var host = await MemberHostAsync();
        var created = await AddAsync(host, NewBody(host));
        var id = created.GetProperty("id").GetString()!;

        var codes = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            using var response = await host.SendAsync(HttpMethod.Post, $"{Base}/{id}/pause", ifMatch: ETag(created));
            return response.StatusCode;
        }));

        Assert.Equal(1, codes.Count(c => c == HttpStatusCode.OK));
        Assert.All(codes.Where(c => c != HttpStatusCode.OK), c => Assert.Equal(HttpStatusCode.PreconditionFailed, c));
        Assert.Equal(created.GetProperty("version").GetInt64() + 1, (await GetAsync(host, id)).GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task Pause_and_resume_on_a_removed_or_unknown_subscription_are_404_and_a_malformed_id_is_404()
    {
        await using var host = await MemberHostAsync();
        var created = await AddAsync(host, NewBody(host));
        var id = created.GetProperty("id").GetString()!;
        using (var removed = await host.SendAsync(HttpMethod.Delete, $"{Base}/{id}", ifMatch: ETag(created)))
        {
            Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        }

        foreach (var target in new[] { id, new string('0', 32), "NOT-AN-ID" })
        {
            foreach (var action in new[] { "pause", "resume" })
            {
                using var response = await host.SendAsync(HttpMethod.Post, $"{Base}/{target}/{action}", ifMatch: ETag(created));
                await ApiTestHost.AssertProblemAsync(response, HttpStatusCode.NotFound, "not-found");
            }
        }
    }

    [Fact]
    public async Task Pause_and_resume_need_the_csrf_header_and_a_matching_origin_like_delete()
    {
        await using var host = await MemberHostAsync();
        var created = await AddAsync(host, NewBody(host));
        var id = created.GetProperty("id").GetString()!;

        foreach (var action in new[] { "pause", "resume" })
        {
            using var noCsrf = await host.SendAsync(HttpMethod.Post, $"{Base}/{id}/{action}", ifMatch: ETag(created), csrf: false);
            await ApiTestHost.AssertProblemAsync(noCsrf, HttpStatusCode.Forbidden, "csrf-header-required");

            using var foreign = await host.SendAsync(HttpMethod.Post, $"{Base}/{id}/{action}", ifMatch: ETag(created), origin: "https://evil.example");
            await ApiTestHost.AssertProblemAsync(foreign, HttpStatusCode.Forbidden, "origin-mismatch");
        }

        Assert.False((await GetAsync(host, id)).GetProperty("paused").GetBoolean());
        Assert.Equal(created.GetProperty("version").GetInt64(), (await GetAsync(host, id)).GetProperty("version").GetInt64());
    }
}
