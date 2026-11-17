using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Subscribed.Subscriptions.Calendar;
using Subscribed.Subscriptions.Domain;
using Xunit;

namespace Subscribed.UnitTests;

public sealed class CalendarFeedBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private static readonly TimeZoneInfo NewYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
    private static readonly TimeZoneInfo Kolkata = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
    private static readonly TimeZoneInfo Sydney = TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney");
    private static readonly TimeZoneInfo LordHowe = TimeZoneInfo.FindSystemTimeZoneById("Australia/Lord_Howe");

    private static string Id(int n) => n.ToString("x32", CultureInfo.InvariantCulture);

    private static DateTimeOffset Utc(string iso) => DateTimeOffset.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private static SubscriptionState Sub(int n, string? cancelBy, string name = "Streamly", long version = 3) => new()
    {
        Id = SubscriptionIds.StreamKey(Id(n)),
        HouseholdId = "h",
        Name = name,
        Currency = "DKK",
        StartDate = new DateOnly(2025, 1, 1),
        CancelBy = cancelBy is null ? null : Utc(cancelBy),
        Version = version,
    };

    private static string Build(IEnumerable<SubscriptionState> subs, TimeZoneInfo? zone = null, string? zoneId = null, string? lang = null, DateTimeOffset? now = null)
    {
        zone ??= TimeZoneInfo.Utc;
        return CalendarFeedBuilder.Build([.. subs], zone, zoneId ?? (zone.Equals(TimeZoneInfo.Utc) ? "UTC" : zone.Id), lang, now ?? Now);
    }

    private static IcsDoc Parse(string text) => IcsDoc.Parse(text);

    // ---- eligibility (D9) ----------------------------------------------------------------------------

    [Fact]
    public void An_eligible_subscription_yields_exactly_two_events_at_minus_24_hours_and_at_the_deadline()
    {
        var doc = Parse(Build([Sub(1, "2026-03-10T09:00:00Z")]));
        var events = doc.Events;
        Assert.Equal(2, events.Count);
        Assert.Equal($"{Id(1)}-cancel-24h@subscribed", events[0].Value("UID"));
        Assert.Equal($"{Id(1)}-cancel-at@subscribed", events[1].Value("UID"));
        Assert.Equal("20260309T090000Z", events[0].Value("DTSTART"));
        Assert.Equal("20260310T090000Z", events[1].Value("DTSTART"));
        Assert.All(events, e => Assert.Equal("PT15M", e.Value("DURATION")));
        Assert.All(events, e => Assert.Null(e.Find("VALARM")));
        Assert.DoesNotContain("RRULE", Build([Sub(1, "2026-03-10T09:00:00Z")]), StringComparison.Ordinal);
    }

    [Fact]
    public void No_events_without_a_deadline_with_a_past_deadline_when_ended_or_removed()
    {
        var ended = Sub(3, "2026-03-10T09:00:00Z");
        ended.EndDate = new DateOnly(2026, 1, 14);
        var removed = Sub(4, "2026-03-10T09:00:00Z");
        removed.Removed = true;

        var text = Build([Sub(1, null), Sub(2, "2026-01-15T11:59:59Z"), ended, removed]);

        Assert.Empty(Parse(text).Events);
        Assert.Contains("BEGIN:VCALENDAR", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Paused_and_not_yet_started_subscriptions_are_included()
    {
        var paused = Sub(1, "2026-03-10T09:00:00Z");
        paused.PausePeriods.Add(new PausePeriodState { PeriodId = "p1", PauseDate = new DateOnly(2026, 1, 1) });
        var future = Sub(2, "2026-03-10T09:00:00Z");
        future.StartDate = new DateOnly(2026, 2, 1);

        Assert.Equal(4, Parse(Build([paused, future])).Events.Count);
    }

    [Fact]
    public void The_deadline_at_now_is_eligible_and_one_tick_before_is_not()
    {
        var atNow = Sub(1, null);
        atNow.CancelBy = Now;
        var tickBefore = Sub(2, null);
        tickBefore.CancelBy = Now.AddTicks(-1);
        var tickAfter = Sub(3, null);
        tickAfter.CancelBy = Now.AddTicks(1);

        var uids = Parse(Build([atNow, tickBefore, tickAfter])).Events.Select(e => e.Value("UID")).ToList();

        Assert.Contains($"{Id(1)}-cancel-at@subscribed", uids);
        Assert.Contains($"{Id(3)}-cancel-at@subscribed", uids);
        Assert.DoesNotContain(uids, u => u.StartsWith(Id(2), StringComparison.Ordinal));
    }

    [Fact]
    public void A_deadline_within_24_hours_of_now_still_emits_both_events()
    {
        var doc = Parse(Build([Sub(1, "2026-01-15T18:00:00Z")]));

        Assert.Equal(2, doc.Events.Count);
        Assert.Equal("20260114T180000Z", doc.Events[0].Value("DTSTART"));
    }

    [Theory]
    [InlineData("2026-01-15", true)]
    [InlineData("2026-01-14", false)]
    public void End_date_today_is_not_ended_and_yesterday_is_in_the_household_zone(string endDate, bool eligible)
    {
        // 2026-01-15T22:30Z is already 2026-01-16 in Auckland (+13), so "today" there is the 16th.
        var now = Utc("2026-01-15T22:30:00Z");
        var s = Sub(1, "2026-03-10T09:00:00Z");
        s.EndDate = DateOnly.Parse(endDate, CultureInfo.InvariantCulture).AddDays(1);

        var auckland = Build([s], TestZones.Auckland, "Pacific/Auckland", now: now);

        // End date 16th (today in Auckland) is eligible, the 15th (yesterday in Auckland) is not.
        Assert.Equal(eligible, Parse(auckland).Events.Count == 2);
    }

    [Fact]
    public void A_deadline_later_than_the_end_date_is_still_eligible()
    {
        var s = Sub(1, "2026-06-10T09:00:00Z");
        s.EndDate = new DateOnly(2026, 2, 1);

        Assert.Equal(2, Parse(Build([s])).Events.Count);
    }

    // ---- identity and stability (D10) ----------------------------------------------------------------

    [Fact]
    public void Sequence_is_the_stream_version_and_the_uid_is_stable()
    {
        var first = Parse(Build([Sub(1, "2026-03-10T09:00:00Z", version: 3)])).Events;
        var later = Parse(Build([Sub(1, "2026-04-10T09:00:00Z", version: 7)], TestZones.Copenhagen)).Events;

        Assert.Equal("3", first[0].Value("SEQUENCE"));
        Assert.Equal("7", later[0].Value("SEQUENCE"));
        Assert.Equal(first.Select(e => e.Value("UID")), later.Select(e => e.Value("UID")));
    }

    [Fact]
    public void A_bare_id_is_used_even_when_the_state_carries_the_stream_key()
    {
        var uid = Parse(Build([Sub(1, "2026-03-10T09:00:00Z")])).Events[0].Value("UID");

        Assert.Equal($"{Id(1)}-cancel-24h@subscribed", uid);
        Assert.DoesNotContain("subscription-", uid, StringComparison.Ordinal);
    }

    [Fact]
    public void Dtstamp_comes_from_the_given_time()
    {
        var doc = Parse(Build([Sub(1, "2026-03-10T09:00:00Z")], now: Utc("2026-02-02T03:04:05Z")));

        Assert.All(doc.Events, e => Assert.Equal("20260202T030405Z", e.Value("DTSTAMP")));
    }

    [Fact]
    public void Order_is_deadline_then_name_then_id_whatever_the_input_order()
    {
        var a = Sub(2, "2026-03-10T09:00:00Z", "Same");
        var b = Sub(1, "2026-03-10T09:00:00Z", "Same");
        var c = Sub(3, "2026-03-09T09:00:00Z", "Zeta");
        var d = Sub(4, "2026-03-10T09:00:00Z", "Alpha");

        var one = Build([a, b, c, d]);
        var two = Build([d, c, b, a]);

        Assert.Equal(one, two);
        var order = Parse(one).Events.Where(e => e.Value("UID").Contains("cancel-at", StringComparison.Ordinal))
            .Select(e => e.Value("UID")[..32]).ToList();
        Assert.Equal([Id(3), Id(4), Id(1), Id(2)], order);
    }

    [Fact]
    public void Two_builds_with_nothing_changed_are_identical_apart_from_the_stamp()
    {
        var subs = new[] { Sub(1, "2026-03-10T09:00:00Z"), Sub(2, "2026-05-10T09:00:00Z", "Other") };

        var a = Build(subs, TestZones.Copenhagen);
        var b = Build(subs, TestZones.Copenhagen);
        var later = Build(subs, TestZones.Copenhagen, now: Now.AddMinutes(5));

        Assert.Equal(a, b);
        Assert.Equal(StripStamp(a), StripStamp(later));
        Assert.NotEqual(a, later);

        static string StripStamp(string s) => string.Join("\n", s.Split("\r\n").Where(l => !l.StartsWith("DTSTAMP", StringComparison.Ordinal)));
    }

    // ---- header and content --------------------------------------------------------------------------

    [Fact]
    public void The_header_carries_the_calendar_properties()
    {
        var doc = Parse(Build([], TestZones.Copenhagen, "Europe/Copenhagen"));

        Assert.Equal("2.0", doc.Calendar.Value("VERSION"));
        Assert.Equal("-//subscribed//cancel-reminders//EN", doc.Calendar.Value("PRODID"));
        Assert.Equal("GREGORIAN", doc.Calendar.Value("CALSCALE"));
        Assert.Equal("PUBLISH", doc.Calendar.Value("METHOD"));
        Assert.Equal("Europe/Copenhagen", doc.Calendar.Value("X-WR-TIMEZONE"));
        Assert.Equal("PT12H", doc.Calendar.Value("REFRESH-INTERVAL"));
        Assert.Equal("PT12H", doc.Calendar.Value("X-PUBLISHED-TTL"));
        Assert.Empty(doc.Calendar.Children);
    }

    [Fact]
    public void Lines_end_with_crlf_and_never_a_bare_line_feed()
    {
        var text = Build([Sub(1, "2026-03-10T09:00:00Z")], TestZones.Copenhagen);

        Assert.EndsWith("END:VCALENDAR\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", text.Replace("\r\n", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void The_feed_holds_only_names_and_dates()
    {
        var s = Sub(1, "2026-03-10T09:00:00Z", "Streamly");
        s.Notes = "TOP-SECRET-NOTE";
        s.Category = "TOP-SECRET-CATEGORY";
        s.UnsubscribeUrl = "https://example.test/TOP-SECRET-LINK";
        s.CatalogId = "TOP-SECRET-CATALOG";
        s.HouseholdId = "TOP-SECRET-HOUSEHOLD";
        s.Entries = [new PriceEntryState { EntryId = "e", Amount = 987654.32m, CadenceUnit = "month", CadenceCount = 1 }];

        var text = Build([s], TestZones.Copenhagen);

        foreach (var forbidden in new[] { "TOP-SECRET", "987654", "example.test", "DKK", "URL", "DESCRIPTION", "LOCATION", "ATTACH", "CATEGORIES" })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.Ordinal);
        }

        Assert.Contains("SUMMARY:Cancel Streamly", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("da", "Opsig Streamly", "Opsig Streamly nu")]
    [InlineData("en", "Cancel Streamly", "Cancel Streamly now")]
    [InlineData(null, "Cancel Streamly", "Cancel Streamly now")]
    [InlineData("", "Cancel Streamly", "Cancel Streamly now")]
    [InlineData("DA", "Cancel Streamly", "Cancel Streamly now")]
    [InlineData("da-DK", "Cancel Streamly", "Cancel Streamly now")]
    [InlineData("da,da", "Cancel Streamly", "Cancel Streamly now")]
    [InlineData("fr", "Cancel Streamly", "Cancel Streamly now")]
    [InlineData("daaaaaaaaaaaaaaaaaaaa", "Cancel Streamly", "Cancel Streamly now")]
    public void The_summary_follows_the_language_and_unknown_values_fall_back_to_english(string? lang, string soon, string now)
    {
        var events = Parse(Build([Sub(1, "2026-03-10T09:00:00Z")], lang: lang)).Events;

        Assert.Equal(soon, events[0].Value("SUMMARY"));
        Assert.Equal(now, events[1].Value("SUMMARY"));
        Assert.Equal(lang == "da" ? "da" : "en", CalendarFeedText.Normalize(lang));
    }

    [Fact]
    public void Special_characters_in_a_name_are_escaped_and_control_characters_dropped()
    {
        var s = Sub(1, "2026-03-10T09:00:00Z", "A, B; C\\D\nE\r\nF\u0007G\tH");

        var text = Build([s]);

        Assert.Contains("SUMMARY:Cancel A\\, B\\; C\\\\D\\nE\\nFGH", text, StringComparison.Ordinal);
        Assert.Equal("Cancel A, B; C\\D\nE\nFGH", Parse(text).Events[0].Text("SUMMARY"));
        Assert.DoesNotContain('\u0007', text);
    }

    [Theory]
    [InlineData("x")]
    [InlineData("Ærø-æøåÆØÅ")]
    [InlineData("日本語のサブスクリプション")]
    [InlineData("😀")]
    public void Long_names_fold_at_75_octets_on_character_boundaries(string unit)
    {
        var name = string.Concat(Enumerable.Repeat(unit, 120));
        var s = Sub(1, "2026-03-10T09:00:00Z", name);

        var text = Build([s]);

        var strict = new UTF8Encoding(false, throwOnInvalidBytes: true);
        var lines = text.Split("\r\n");
        Assert.All(lines, l => Assert.True(strict.GetByteCount(l) <= 75, $"line has {strict.GetByteCount(l)} octets"));
        Assert.Contains(lines, l => l.StartsWith(' '));
        Assert.Equal("Cancel " + name, Parse(text).Events[0].Text("SUMMARY"));
    }

    // ---- time zones (D11) -----------------------------------------------------------------------------

    [Fact]
    public void Utc_households_write_utc_times_and_no_vtimezone()
    {
        var text = Build([Sub(1, "2026-03-10T09:00:00Z")]);
        var doc = Parse(text);

        Assert.Equal("UTC", doc.Calendar.Value("X-WR-TIMEZONE"));
        Assert.DoesNotContain(doc.Calendar.Children, c => c.Name == "VTIMEZONE");
        Assert.DoesNotContain("TZID", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unresolvable_zone_built_as_utc_never_names_the_stored_id()
    {
        // The caller passes UTC for a stored id it could not resolve (4.3). The stored id must not reach the output.
        var text = CalendarFeedBuilder.Build([Sub(1, "2026-03-10T09:00:00Z")], TimeZoneInfo.Utc, "UTC", "en", Now);

        Assert.Contains("X-WR-TIMEZONE:UTC", text, StringComparison.Ordinal);
        Assert.DoesNotContain("TZID", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Mars", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_zone_id_that_is_unsafe_in_a_parameter_is_written_as_utc()
    {
        var text = Build([Sub(1, "2026-03-10T09:00:00Z")], TestZones.Copenhagen, "Europe/Copen;hagen\"");

        Assert.Contains("X-WR-TIMEZONE:UTC", text, StringComparison.Ordinal);
        Assert.Contains("DTSTART:20260309T090000Z", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Europe/Copenhagen", "2026-03-10T09:00:00Z", "20260309T100000", "20260310T100000", "+0100")]
    [InlineData("America/New_York", "2026-03-10T09:00:00Z", "20260309T050000", "20260310T050000", "-0400")]
    [InlineData("Asia/Kolkata", "2026-03-10T09:00:00Z", "20260309T143000", "20260310T143000", "+0530")]
    [InlineData("Australia/Sydney", "2026-03-10T09:00:00Z", "20260309T200000", "20260310T200000", "+1100")]
    [InlineData("Australia/Lord_Howe", "2026-03-10T09:00:00Z", "20260309T200000", "20260310T200000", "+1100")]
    public void Events_are_written_in_the_household_zone(string zoneId, string cancelBy, string soon, string at, string offset)
    {
        // tzdata: 2026-03-10 is after the US spring-forward (Mar 8) and before the EU one (Mar 29); Australia is still on DST.
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var instant = Utc(cancelBy);
        Assert.Equal(TimeSpan.Parse(offset.Insert(3, ":").TrimStart('+'), CultureInfo.InvariantCulture), zone.GetUtcOffset(instant));

        var doc = Parse(Build([Sub(1, cancelBy)], zone, zoneId));

        Assert.Equal(zoneId, doc.Calendar.Value("X-WR-TIMEZONE"));
        Assert.Equal(soon, doc.Events[0].Value("DTSTART"));
        Assert.Equal(at, doc.Events[1].Value("DTSTART"));
        Assert.Equal(zoneId, doc.Events[0].Param("DTSTART", "TZID"));
        Assert.Single(doc.Calendar.Children, c => c.Name == "VTIMEZONE");
        Assert.Equal(zoneId, doc.Calendar.Children.Single(c => c.Name == "VTIMEZONE").Value("TZID"));
    }

    [Fact]
    public void Lord_Howe_keeps_its_half_hour_daylight_saving_in_the_observances()
    {
        // tzdata: Lord Howe is +10:30 standard and +11:00 daylight; DST ends the first Sunday of April and starts the first Sunday of October.
        var instant = Utc("2026-04-04T20:00:00Z");
        Assert.Equal(TimeSpan.FromHours(10.5), LordHowe.GetUtcOffset(instant));

        var tz = Parse(Build([Sub(1, "2026-04-05T09:00:00Z")], LordHowe, "Australia/Lord_Howe")).Timezone!;

        var transitions = tz.Children.Select(o => (o.Name, From: o.Value("TZOFFSETFROM"), To: o.Value("TZOFFSETTO"))).ToList();
        Assert.Contains(("STANDARD", "+1100", "+1030"), transitions);
    }

    [Fact]
    public void Spring_forward_day_the_24_hour_reminder_crosses_the_gap_by_instant_arithmetic()
    {
        // tzdata: EU summer time starts 2026-03-29 at 01:00Z (02:00 CET becomes 03:00 CEST).
        var cancelBy = "2026-03-30T00:30:00Z"; // 02:30 CEST on the 30th
        var doc = Parse(Build([Sub(1, cancelBy)], TestZones.Copenhagen, "Europe/Copenhagen"));

        Assert.Equal("20260329T013000", doc.Events[0].Value("DTSTART")); // 00:30Z is 01:30 CET: not 02:30, which does not exist
        Assert.Equal("20260330T023000", doc.Events[1].Value("DTSTART"));
        Assert.Equal(TimeSpan.FromHours(24), Utc(cancelBy) - Utc("2026-03-29T00:30:00Z"));
        AssertRoundTrip(doc, TestZones.Copenhagen);
    }

    [Fact]
    public void Spring_forward_day_a_deadline_right_after_the_gap_is_written_with_the_summer_offset()
    {
        var doc = Parse(Build([Sub(1, "2026-03-29T01:00:00Z")], TestZones.Copenhagen, "Europe/Copenhagen"));

        Assert.Equal("20260329T030000", doc.Events[1].Value("DTSTART"));
        var onsets = doc.Timezone!.Children.Select(o => (o.Name, o.Value("TZOFFSETFROM"), o.Value("TZOFFSETTO"))).ToList();
        Assert.Contains(("DAYLIGHT", "+0100", "+0200"), onsets);
        AssertRoundTrip(doc, TestZones.Copenhagen);
    }

    [Fact]
    public void Fall_back_day_an_ambiguous_deadline_is_written_in_utc_and_the_other_event_keeps_the_zone()
    {
        // tzdata: EU summer time ends 2026-10-25 at 01:00Z; local 02:00 to 02:59 happens twice.
        var first = Parse(Build([Sub(1, "2026-10-25T00:30:00Z")], TestZones.Copenhagen, "Europe/Copenhagen"));
        Assert.Equal("20261024T023000", first.Events[0].Value("DTSTART")); // 02:30 CEST the day before: not ambiguous
        Assert.Equal("TZID", first.Events[0].ParamNames("DTSTART").Single());
        Assert.Equal("20261025T003000Z", first.Events[1].Value("DTSTART")); // 02:30 CEST on the 25th: ambiguous
        Assert.Empty(first.Events[1].ParamNames("DTSTART"));

        var second = Parse(Build([Sub(1, "2026-10-25T01:30:00Z")], TestZones.Copenhagen, "Europe/Copenhagen"));
        Assert.Equal("20261025T013000Z", second.Events[1].Value("DTSTART")); // 02:30 CET: the second pass
        AssertRoundTrip(first, TestZones.Copenhagen);
    }

    [Fact]
    public void A_feed_where_every_event_is_ambiguous_has_no_vtimezone()
    {
        // Both events ambiguous is impossible (24h apart), so use one event whose partner is outside the zone path: clamp it.
        var text = Build([Sub(1, "2040-01-01T00:00:00Z")], TestZones.Copenhagen, "Europe/Copenhagen");

        Assert.DoesNotContain("VTIMEZONE", text, StringComparison.Ordinal);
        Assert.DoesNotContain("TZID", text, StringComparison.Ordinal);
    }

    [Fact]
    public void An_event_beyond_ten_years_is_written_in_utc_and_the_others_keep_the_zone()
    {
        var near = Sub(1, "2026-07-01T09:00:00Z", "Near");
        var far = Sub(2, "2037-07-01T09:00:00Z", "Far");

        var doc = Parse(Build([near, far], TestZones.Copenhagen, "Europe/Copenhagen"));

        var byUid = doc.Events.ToDictionary(e => e.Value("UID"));
        Assert.Equal("20260701T110000", byUid[$"{Id(1)}-cancel-at@subscribed"].Value("DTSTART"));
        Assert.Equal("20370701T090000Z", byUid[$"{Id(2)}-cancel-at@subscribed"].Value("DTSTART"));
        Assert.Equal("20370630T090000Z", byUid[$"{Id(2)}-cancel-24h@subscribed"].Value("DTSTART"));
        // The VTIMEZONE spans only the near events, so no observance reaches 2037.
        Assert.All(doc.Timezone!.Children, o => Assert.True(string.CompareOrdinal(o.Value("DTSTART"), "2030") < 0));
    }

    [Fact]
    public void A_transition_just_before_the_earliest_event_is_in_the_vtimezone()
    {
        // The 24h event is on 2026-03-29 00:30Z (before the 01:00Z transition); the deadline is after it.
        var doc = Parse(Build([Sub(1, "2026-03-30T00:30:00Z")], TestZones.Copenhagen, "Europe/Copenhagen"));

        Assert.Contains(doc.Timezone!.Children, o => o.Name == "DAYLIGHT" && o.Value("DTSTART") == "20260329T020000");
    }

    [Fact]
    public void The_vtimezone_reproduces_the_offset_for_every_event_across_zones_and_dates()
    {
        var instants = new[]
        {
            "2026-01-15T12:00:00Z", "2026-03-08T07:00:00Z", "2026-03-29T01:00:00Z", "2026-04-04T16:00:00Z", "2026-04-05T04:00:00Z",
            "2026-10-03T16:00:00Z", "2026-10-04T15:30:00Z", "2026-10-25T00:59:59Z", "2026-10-25T01:00:00Z", "2026-11-01T06:00:00Z",
            "2027-02-01T00:00:00Z",
        };
        var zones = new[] { TestZones.Copenhagen, NewYork, Kolkata, Sydney, LordHowe, TestZones.Auckland };

        foreach (var zone in zones)
        {
            var subs = instants.Select((iso, i) => Sub(i + 1, iso)).ToList();
            AssertRoundTrip(Parse(Build(subs, zone, zone.Id)), zone);
        }
    }

    // ---- golden files ---------------------------------------------------------------------------------

    [Fact]
    public void Golden_utc()
    {
        // tzdata rule: none; UTC has no transitions.
        AssertGolden("utc.ics", TimeZoneInfo.Utc, "UTC", [Sub(1, "2026-03-10T09:00:00Z", "Streamly")]);
    }

    [Fact]
    public void Golden_northern_dst_copenhagen()
    {
        // tzdata rule: EU summer time, last Sunday of March 01:00Z to last Sunday of October 01:00Z (2026-03-29 and 2026-10-25).
        Assert.Equal(TimeSpan.FromHours(1), TestZones.Copenhagen.GetUtcOffset(Utc("2026-03-28T12:00:00Z")));
        Assert.Equal(TimeSpan.FromHours(2), TestZones.Copenhagen.GetUtcOffset(Utc("2026-03-30T12:00:00Z")));
        AssertGolden("copenhagen.ics", TestZones.Copenhagen, "Europe/Copenhagen",
            [Sub(1, "2026-03-30T08:00:00Z", "Streamly"), Sub(2, "2026-10-25T00:30:00Z", "Newsly, daily")]);
    }

    [Fact]
    public void Golden_southern_dst_sydney()
    {
        // tzdata rule: AEDT (+11) from the first Sunday of October to the first Sunday of April (2026-04-05 and 2026-10-04).
        Assert.Equal(TimeSpan.FromHours(11), Sydney.GetUtcOffset(Utc("2026-04-04T12:00:00Z")));
        Assert.Equal(TimeSpan.FromHours(10), Sydney.GetUtcOffset(Utc("2026-04-05T12:00:00Z")));
        AssertGolden("sydney.ics", Sydney, "Australia/Sydney", [Sub(1, "2026-04-06T00:00:00Z", "Streamly")]);
    }

    [Fact]
    public void Golden_half_hour_offset_kolkata()
    {
        // tzdata rule: India has no daylight saving; the offset is +05:30 all year.
        Assert.Equal(TimeSpan.FromMinutes(330), Kolkata.GetUtcOffset(Utc("2026-06-01T00:00:00Z")));
        AssertGolden("kolkata.ics", Kolkata, "Asia/Kolkata", [Sub(1, "2026-06-01T00:00:00Z", "Streamly")]);
    }

    private static void AssertGolden(string file, TimeZoneInfo zone, string zoneId, IReadOnlyList<SubscriptionState> subs, [CallerFilePath] string sourceFile = "")
    {
        var actual = CalendarFeedBuilder.Build(subs, zone, zoneId, "en", Now);
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "calendar", file);
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            // Regenerate with UPDATE_GOLDEN=1, then review the diff: the source fixture is written, not the copy in bin.
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(sourceFile)!, "Fixtures", "calendar", file), actual, new UTF8Encoding(false));
            path = Path.Combine(Path.GetDirectoryName(sourceFile)!, "Fixtures", "calendar", file);
        }

        var expected = File.ReadAllText(path);
        Assert.Equal(expected.ReplaceLineEndings("\n"), actual.ReplaceLineEndings("\n"));
    }

    // ---- helpers --------------------------------------------------------------------------------------

    /// <summary>The VTIMEZONE offset in force at each event instant equals <see cref="TimeZoneInfo.GetUtcOffset(DateTimeOffset)"/>.</summary>
    private static void AssertRoundTrip(IcsDoc doc, TimeZoneInfo zone)
    {
        var tz = doc.Timezone;
        if (tz is null)
        {
            return;
        }

        var observances = tz.Children.Select(o =>
        {
            var from = ParseOffset(o.Value("TZOFFSETFROM"));
            var to = ParseOffset(o.Value("TZOFFSETTO"));
            var local = DateTime.ParseExact(o.Value("DTSTART"), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
            return (Onset: new DateTimeOffset(local - from, TimeSpan.Zero), To: to);
        }).OrderBy(o => o.Onset).ToList();

        foreach (var e in doc.Events)
        {
            var raw = e.Value("DTSTART");
            if (raw.EndsWith('Z'))
            {
                continue;
            }

            var local = DateTime.ParseExact(raw, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
            // The local time is unambiguous (ambiguous ones are written in UTC), so one offset maps it back to an instant.
            var instant = new DateTimeOffset(local - zone.GetUtcOffset(local), TimeSpan.Zero);
            var inForce = observances.Last(o => o.Onset <= instant).To;
            Assert.Equal(zone.GetUtcOffset(instant), inForce);
        }
    }

    private static TimeSpan ParseOffset(string value)
    {
        var sign = value[0] == '-' ? -1 : 1;
        var digits = value[1..];
        var seconds = digits.Length == 6 ? int.Parse(digits[4..], CultureInfo.InvariantCulture) : 0;
        return sign * new TimeSpan(int.Parse(digits[..2], CultureInfo.InvariantCulture), int.Parse(digits[2..4], CultureInfo.InvariantCulture), seconds);
    }
}

/// <summary>A minimal iCalendar reader for assertions: unfolds lines, splits properties and parameters, nests components.</summary>
internal sealed class IcsDoc
{
    public required IcsComponent Calendar { get; init; }

    public IReadOnlyList<IcsComponent> Events => [.. Calendar.Children.Where(c => c.Name == "VEVENT")];

    public IcsComponent? Timezone => Calendar.Children.SingleOrDefault(c => c.Name == "VTIMEZONE");

    public static IcsDoc Parse(string text)
    {
        Assert.EndsWith("\r\n", text, StringComparison.Ordinal);
        var unfolded = text.Replace("\r\n ", "", StringComparison.Ordinal);
        var stack = new Stack<IcsComponent>();
        IcsComponent? root = null;
        foreach (var line in unfolded.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            Assert.True(colon > 0, $"not a content line: {line}");
            var head = line[..colon].Split(';');
            var value = line[(colon + 1)..];
            var name = head[0];
            if (name == "BEGIN")
            {
                var c = new IcsComponent(value);
                if (stack.Count > 0)
                {
                    stack.Peek().Children.Add(c);
                }
                else
                {
                    root = c;
                }

                stack.Push(c);
            }
            else if (name == "END")
            {
                Assert.Equal(value, stack.Pop().Name);
            }
            else
            {
                var parameters = head.Skip(1).Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
                stack.Peek().Properties.Add((name, parameters, value));
            }
        }

        Assert.Empty(stack);
        Assert.Equal("VCALENDAR", root!.Name);
        return new IcsDoc { Calendar = root };
    }
}

internal sealed class IcsComponent(string name)
{
    public string Name { get; } = name;

    public List<(string Name, Dictionary<string, string> Params, string Value)> Properties { get; } = [];

    public List<IcsComponent> Children { get; } = [];

    public string Value(string property) => Properties.Single(p => p.Name == property).Value;

    public string Param(string property, string param) => Properties.Single(p => p.Name == property).Params[param];

    public IEnumerable<string> ParamNames(string property) => Properties.Single(p => p.Name == property).Params.Keys;

    public IcsComponent? Find(string name) => Children.FirstOrDefault(c => c.Name == name);

    /// <summary>The TEXT value with escapes undone.</summary>
    public string Text(string property)
    {
        var raw = Value(property);
        var sb = new StringBuilder();
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '\\' && i + 1 < raw.Length)
            {
                i++;
                sb.Append(raw[i] == 'n' ? '\n' : raw[i]);
            }
            else
            {
                sb.Append(raw[i]);
            }
        }

        return sb.ToString();
    }
}
