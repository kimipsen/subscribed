using System.Text;
using System.Text.Json;
using Subscribed.Subscriptions;
using Subscribed.Subscriptions.Domain;
using Xunit;

namespace Subscribed.UnitTests;

/// <summary>The one https-only rule, shared by user input and the catalog loader (issue #35).</summary>
public static class UnsubscribeUrlCases
{
    private static string Padded(int length) => "https://example.com/" + new string('a', length - "https://example.com/".Length);

    /// <summary>Accepted values are stored exactly as typed (trimmed only): never through <see cref="Uri"/>.</summary>
    public static TheoryData<string> Valid => new()
    {
        "https://example.com",
        "https://Example.com/Path?q=1#frag",
        "HTTPS://x.example",
        "Https://x.example/cancel",
        "https://localhost/cancel",
        "https://127.0.0.1/cancel",
        "https://[::1]/cancel",
        "https://xn--bcher-kva.example/cancel",
        "https://example.com:8443/cancel",
        Padded(2048),
    };

    public static TheoryData<string> Invalid => new()
    {
        "http://example.com",
        "javascript:alert(1)",
        "/relative/path",
        "example.com/cancel",
        "not a url",
        "https:/x",
        "https:\\\\x",
        "https://",
        "https:///x",
        "https:example.com",
        "http://",
        Padded(2049),
        "https://example.com/can cel",
        "https://example.com/can\tcel",
        "https://example.com/can\ncel",
        "https://example.com/can\u00A0cel",
        "https://example.com/can\u2028cel",
        "https://u@a.example",
        "https://user:pass@a.example/x",
        "https://a.example%2F@b",
        "https://a.example\\@b.example",
        "https://example.com/a\\b",
    };
}

public sealed class UnsubscribeUrlRuleTests
{
    [Theory]
    [MemberData(nameof(UnsubscribeUrlCases.Valid), MemberType = typeof(UnsubscribeUrlCases))]
    public void Valid_values_are_accepted_and_stored_as_typed(string raw)
    {
        Assert.True(SubscriptionText.TryNormalizeUnsubscribeUrl(raw, out var url));
        Assert.Equal(raw, url);
    }

    [Theory]
    [MemberData(nameof(UnsubscribeUrlCases.Invalid), MemberType = typeof(UnsubscribeUrlCases))]
    public void Invalid_values_are_rejected(string raw)
    {
        Assert.False(SubscriptionText.TryNormalizeUnsubscribeUrl(raw, out var url));
        Assert.Null(url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\u00A0\u00A0")]
    public void Empty_or_whitespace_only_means_absent(string? raw)
    {
        Assert.True(SubscriptionText.TryNormalizeUnsubscribeUrl(raw, out var url));
        Assert.Null(url);
    }

    [Fact]
    public void Surrounding_whitespace_including_nbsp_is_trimmed_and_the_rest_kept_verbatim()
    {
        // string.Trim removes Unicode white space at both ends (NBSP too); only the inside is checked for white space.
        Assert.True(SubscriptionText.TryNormalizeUnsubscribeUrl("\u00A0 https://Example.com/Path \u00A0", out var url));
        Assert.Equal("https://Example.com/Path", url);
    }

    [Fact]
    public void Stored_value_is_the_raw_string_not_the_uri_form()
    {
        Assert.True(SubscriptionText.TryNormalizeUnsubscribeUrl("https://Example.com", out var url));
        Assert.Equal("https://Example.com", url);
        Assert.NotEqual(new Uri("https://Example.com").AbsoluteUri, url);
    }

    [Theory]
    [MemberData(nameof(UnsubscribeUrlCases.Valid), MemberType = typeof(UnsubscribeUrlCases))]
    public void Domain_accepts_what_the_rule_accepts(string raw)
    {
        var parsed = SubscriptionDecisions.ParseAdd(SubscriptionDecisionTests.Input() with { UnsubscribeUrl = raw }, TestZones.Utc);
        Assert.Equal(raw, parsed.UnsubscribeUrl);
        Assert.Equal(raw, SubscriptionDecisions.ParseUpdate(Update(raw), TestZones.Utc).UnsubscribeUrl);
    }

    [Theory]
    [MemberData(nameof(UnsubscribeUrlCases.Invalid), MemberType = typeof(UnsubscribeUrlCases))]
    public void Domain_rejects_with_the_field_key_and_never_echoes_the_value(string raw)
    {
        var add = Assert.Throws<SubscriptionValidationException>(() => SubscriptionDecisions.ParseAdd(SubscriptionDecisionTests.Input() with { UnsubscribeUrl = raw }, TestZones.Utc));
        var update = Assert.Throws<SubscriptionValidationException>(() => SubscriptionDecisions.ParseUpdate(Update(raw), TestZones.Utc));
        foreach (var ex in new[] { add, update })
        {
            var error = Assert.Single(ex.Errors);
            Assert.Equal("unsubscribeUrl", error.Key);
            if (raw.Length > "https://".Length)
            {
                // The generic message itself says "https://", so a bare scheme cannot be checked for an echo.
                Assert.DoesNotContain(error.Value, m => m.Contains(raw, StringComparison.Ordinal));
            }
        }
    }

    [Theory]
    [MemberData(nameof(UnsubscribeUrlCases.Valid), MemberType = typeof(UnsubscribeUrlCases))]
    public void Loader_accepts_what_the_rule_accepts(string raw) =>
        Assert.Equal(raw, LoadWith(JsonSerializer.Serialize(raw)).UnsubscribeUrl);

    [Theory]
    [MemberData(nameof(UnsubscribeUrlCases.Invalid), MemberType = typeof(UnsubscribeUrlCases))]
    public void Loader_rejects_what_the_rule_rejects_and_names_service_and_field_but_not_the_value(string raw)
    {
        var ex = Assert.Throws<CatalogValidationException>(() => LoadWith(JsonSerializer.Serialize(raw)));
        Assert.Contains("'streamco'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'unsubscribeUrl'", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(raw, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Loader_accepts_absence_and_null()
    {
        Assert.Null(LoadWith(null).UnsubscribeUrl);
        Assert.Null(LoadWith("null").UnsubscribeUrl);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void Loader_rejects_a_non_string_kind(string json) =>
        Assert.Contains("'unsubscribeUrl'", Assert.Throws<CatalogValidationException>(() => LoadWith(json)).Message, StringComparison.Ordinal);

    [Theory]
    [InlineData(" https://example.com/cancel")]
    [InlineData("https://example.com/cancel ")]
    [InlineData("\u00A0https://example.com/cancel")]
    [InlineData("")]
    [InlineData("   ")]
    public void Loader_rejects_untrimmed_or_empty_values_like_name_and_category(string raw) =>
        Assert.Throws<CatalogValidationException>(() => LoadWith(JsonSerializer.Serialize(raw)));

    private static UpdateInput Update(string raw) => new("StreamCo", null, null, "DKK", "2024-03-01", null, null, raw);

    private static CatalogService LoadWith(string? jsonValue)
    {
        var property = jsonValue is null ? "" : $", \"unsubscribeUrl\": {jsonValue}";
        var json = $$"""
            { "schemaVersion": 1, "services": [
              { "id": "streamco", "name": "StreamCo", "category": "Streaming", "logo": null,
                "lastVerified": "2026-11-14", "sourceUrl": "https://example.com/pricing"{{property}}, "plans": [] } ] }
            """;
        return CatalogLoader.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)), new DateOnly(2026, 11, 15)).TryGet("streamco")!;
    }

    [Fact]
    public void Shipped_catalog_urls_pass_the_shared_rule()
    {
        var catalog = CatalogLoader.LoadEmbedded(TimeProvider.System);
        foreach (var service in catalog.Services.Where(s => s.UnsubscribeUrl is not null))
        {
            Assert.True(SubscriptionText.TryNormalizeUnsubscribeUrl(service.UnsubscribeUrl, out var url), service.Id);
            Assert.Equal(service.UnsubscribeUrl, url);
        }

        // Zero populated URLs is allowed: no official cancel page could be confirmed offline (see the PR description), so
        // there is deliberately no "at least one service carries a URL" assertion.
    }
}

public sealed class UnsubscribeUrlEventTests
{
    private static readonly DateTimeOffset Now = SubscriptionDecisionTests.Now;
    private const string Cancel = "https://example.com/cancel";

    private static string Fixture(string folder, string alias) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", folder, alias + ".json")).Trim();

    [Fact]
    public void Top_level_fixtures_carry_the_new_field()
    {
        var added = (SubscriptionAdded)SubscriptionEventTypes.Deserialize(SubscriptionEventTypes.Added, Fixture("events", SubscriptionEventTypes.Added));
        var changed = (SubscriptionDetailsChanged)SubscriptionEventTypes.Deserialize(SubscriptionEventTypes.DetailsChanged, Fixture("events", SubscriptionEventTypes.DetailsChanged));
        Assert.Equal(Cancel, added.UnsubscribeUrl);
        Assert.Equal(Cancel, changed.UnsubscribeUrl);
    }

    [Fact]
    public void Legacy_payloads_without_the_key_deserialize_through_the_real_path_with_a_null_url_and_rehydrate()
    {
        var addedJson = Fixture(Path.Combine("events", "legacy"), SubscriptionEventTypes.Added);
        var changedJson = Fixture(Path.Combine("events", "legacy"), SubscriptionEventTypes.DetailsChanged);
        Assert.False(JsonElement.Parse(addedJson).TryGetProperty("unsubscribeUrl", out _));
        Assert.False(JsonElement.Parse(changedJson).TryGetProperty("unsubscribeUrl", out _));

        var added = (SubscriptionAdded)SubscriptionEventTypes.Deserialize(SubscriptionEventTypes.Added, addedJson);
        var changed = (SubscriptionDetailsChanged)SubscriptionEventTypes.Deserialize(SubscriptionEventTypes.DetailsChanged, changedJson);
        Assert.Null(added.UnsubscribeUrl);
        Assert.Null(changed.UnsubscribeUrl);

        var state = SubscriptionState.Rehydrate([added, changed]);
        Assert.Equal("StreamCo Plus", state.Name);
        Assert.Null(state.UnsubscribeUrl);
    }

    [Fact]
    public void A_null_url_round_trips_as_an_explicit_null_key()
    {
        var e = new SubscriptionDetailsChanged("StreamCo", null, null, Now);
        var json = SubscriptionEventTypes.Serialize(e);
        Assert.Equal(JsonValueKind.Null, JsonElement.Parse(json).GetProperty("unsubscribeUrl").ValueKind);
        Assert.Equal(e, SubscriptionEventTypes.Deserialize(SubscriptionEventTypes.DetailsChanged, json));
    }

    [Fact]
    public void An_old_shape_details_changed_after_a_new_shape_added_clears_the_url_on_replay()
    {
        // Accepted transient limitation (rolling deploy): an old node writes details_changed without the key, so replay assigns null.
        var added = (SubscriptionAdded)SubscriptionEventTypes.Deserialize(SubscriptionEventTypes.Added, Fixture("events", SubscriptionEventTypes.Added));
        var oldShape = (SubscriptionDetailsChanged)SubscriptionEventTypes.Deserialize(SubscriptionEventTypes.DetailsChanged, Fixture(Path.Combine("events", "legacy"), SubscriptionEventTypes.DetailsChanged));
        Assert.Equal(Cancel, SubscriptionState.Rehydrate([added]).UnsubscribeUrl);
        Assert.Null(SubscriptionState.Rehydrate([added, oldShape]).UnsubscribeUrl);
    }

    [Fact]
    public void New_events_put_the_field_after_recorded_at()
    {
        foreach (var type in new[] { typeof(SubscriptionAdded), typeof(SubscriptionDetailsChanged) })
        {
            var names = type.GetConstructors().Single().GetParameters().Select(p => p.Name).ToList();
            Assert.Equal("UnsubscribeUrl", names[^1]);
            Assert.Equal("RecordedAt", names[^2]);
        }
    }
}

public sealed class UnsubscribeUrlDecisionTests
{
    private static readonly DateTimeOffset Now = SubscriptionDecisionTests.Now;
    private const string Cancel = "https://example.com/cancel";

    private static List<object> History(string? url = null) =>
        SubscriptionDecisionTests.Added(SubscriptionDecisionTests.Input() with { UnsubscribeUrl = url });

    private static DetailsUpdate Same(SubscriptionState s, string? name = null, string? notes = null, string? url = null) =>
        new(name ?? s.Name, s.Category, notes ?? s.Notes, s.Currency, s.StartDate, s.EndDate, s.CatalogId, url);

    [Fact]
    public void Add_with_a_url_emits_it_on_the_added_event()
    {
        var added = Assert.IsType<SubscriptionAdded>(History(Cancel)[0]);
        Assert.Equal(Cancel, added.UnsubscribeUrl);
        Assert.Equal(Cancel, SubscriptionState.Rehydrate(History(Cancel)).UnsubscribeUrl);
    }

    [Fact]
    public void Add_without_a_url_stores_null() => Assert.Null(SubscriptionState.Rehydrate(History()).UnsubscribeUrl);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Whitespace_only_becomes_null_on_add_and_update(string raw)
    {
        Assert.Null(SubscriptionDecisions.ParseAdd(SubscriptionDecisionTests.Input() with { UnsubscribeUrl = raw }, TestZones.Utc).UnsubscribeUrl);
        Assert.Null(SubscriptionDecisions.ParseUpdate(new UpdateInput("StreamCo", null, null, "DKK", "2024-03-01", null, null, raw), TestZones.Utc).UnsubscribeUrl);
    }

    [Fact]
    public void Url_only_update_emits_details_changed_with_all_four_fields()
    {
        var history = History();
        var state = SubscriptionState.Rehydrate(history);
        var changed = Assert.IsType<SubscriptionDetailsChanged>(Assert.Single(SubscriptionDecisions.UpdateDetails(history, Same(state, url: Cancel), Now)));
        Assert.Equal(("StreamCo", null, null, Cancel), (changed.Name, changed.Category, changed.Notes, changed.UnsubscribeUrl));
    }

    [Fact]
    public void Unchanged_url_emits_nothing()
    {
        var history = History(Cancel);
        Assert.Empty(SubscriptionDecisions.UpdateDetails(history, Same(SubscriptionState.Rehydrate(history), url: Cancel), Now));
    }

    [Fact]
    public void Clearing_with_null_emits_details_changed_and_clears_the_state()
    {
        var history = History(Cancel);
        var events = SubscriptionDecisions.UpdateDetails(history, Same(SubscriptionState.Rehydrate(history), url: null), Now);
        Assert.Null(Assert.IsType<SubscriptionDetailsChanged>(Assert.Single(events)).UnsubscribeUrl);
        Assert.Null(SubscriptionState.Rehydrate(history.Concat(events)).UnsubscribeUrl);
    }

    [Theory]
    [InlineData("StreamCo 2", null)]
    [InlineData(null, "A note")]
    public void Name_only_and_notes_only_updates_restate_the_current_url(string? name, string? notes)
    {
        var history = History(Cancel);
        var events = SubscriptionDecisions.UpdateDetails(history, Same(SubscriptionState.Rehydrate(history), name, notes, Cancel), Now);
        Assert.Equal(Cancel, Assert.IsType<SubscriptionDetailsChanged>(Assert.Single(events)).UnsubscribeUrl);
        Assert.Equal(Cancel, SubscriptionState.Rehydrate(history.Concat(events)).UnsubscribeUrl);
    }

    [Fact]
    public void Non_https_is_rejected_with_the_unsubscribe_url_key()
    {
        var ex = Assert.Throws<SubscriptionValidationException>(() => SubscriptionDecisions.ParseAdd(SubscriptionDecisionTests.Input() with { UnsubscribeUrl = "http://example.com" }, TestZones.Utc));
        Assert.Equal("unsubscribeUrl", Assert.Single(ex.Errors).Key);
    }
}

public sealed class UnsubscribeLinkResolutionTests
{
    private const string UserUrl = "https://user.example/cancel";
    private const string CatalogUrl = "https://catalog.example/cancel";

    private static Catalog CatalogWith(string? url) => new([
        new CatalogService("streamco", "StreamCo", "Streaming", null, new DateOnly(2026, 11, 1), "https://example.com/pricing", [], url),
    ]);

    private static SubscriptionState State(string? userUrl, string? catalogId = "streamco") =>
        new() { Id = "subscription-1", UnsubscribeUrl = userUrl, CatalogId = catalogId };

    [Fact]
    public void User_url_wins_over_the_catalog_url() => Assert.Equal(UserUrl, UnsubscribeLink.Effective(State(UserUrl), CatalogWith(CatalogUrl)));

    [Fact]
    public void Catalog_url_is_used_when_the_user_url_is_null() => Assert.Equal(CatalogUrl, UnsubscribeLink.Effective(State(null), CatalogWith(CatalogUrl)));

    [Fact]
    public void None_when_both_are_null() => Assert.Null(UnsubscribeLink.Effective(State(null), CatalogWith(null)));

    [Fact]
    public void None_when_there_is_no_link_and_no_user_url() => Assert.Null(UnsubscribeLink.Effective(State(null, catalogId: null), CatalogWith(CatalogUrl)));

    [Fact]
    public void A_dangling_catalog_id_yields_none_but_the_user_url_still_wins()
    {
        Assert.Null(UnsubscribeLink.Effective(State(null, "gone"), CatalogWith(CatalogUrl)));
        Assert.Equal(UserUrl, UnsubscribeLink.Effective(State(UserUrl, "gone"), CatalogWith(CatalogUrl)));
    }

    [Fact]
    public void Paused_and_removed_style_states_still_resolve()
    {
        var paused = State(null);
        paused.Paused = true;
        paused.EndDate = new DateOnly(2020, 1, 1);
        Assert.Equal(CatalogUrl, UnsubscribeLink.Effective(paused, CatalogWith(CatalogUrl)));
    }

    [Fact]
    public void The_detail_dto_uses_the_shared_resolution()
    {
        var added = SubscriptionDecisionTests.Added(SubscriptionDecisionTests.Input() with { UnsubscribeUrl = null });
        var state = SubscriptionState.Rehydrate(added);
        state.CatalogId = "streamco";
        var dto = SubscriptionDetailDto.From(state, SubscriptionDecisionTests.Today, CatalogWith(CatalogUrl), TimeZoneInfo.Utc);
        Assert.Null(dto.UnsubscribeUrl);
        Assert.Equal(CatalogUrl, dto.EffectiveUnsubscribeUrl);
        Assert.Equal(CatalogUrl, CatalogDto.From(CatalogWith(CatalogUrl)).Services[0].UnsubscribeUrl);
    }
}
