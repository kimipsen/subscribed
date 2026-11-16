using System.Text.Json;
using Subscribed.Subscriptions.Domain;
using Xunit;

namespace Subscribed.UnitTests;

public sealed class SubscriptionCatalogLinkTests
{
    private static readonly DateTimeOffset Now = SubscriptionDecisionTests.Now;

    private static AddInput Input(string? catalogId) =>
        SubscriptionDecisionTests.Input() with { CatalogId = catalogId };

    private static DetailsUpdate Update(SubscriptionState current, string? catalogId) =>
        new(current.Name, current.Category, current.Notes, current.Currency, current.StartDate, current.EndDate, catalogId);

    private static List<object> Linked(string catalogId = "streamco") => SubscriptionDecisionTests.Added(Input(catalogId));

    [Fact]
    public void Rehydrating_a_stream_with_a_catalog_link_sets_the_id_and_counts_the_version()
    {
        var state = SubscriptionState.Rehydrate(Linked());
        Assert.Equal("streamco", state.CatalogId);
        Assert.Equal(2, state.Version);
    }

    [Fact]
    public void Add_with_a_catalog_id_emits_added_then_linked()
    {
        var events = Linked();
        Assert.Collection(events, e => Assert.IsType<SubscriptionAdded>(e), e => Assert.Equal("streamco", Assert.IsType<SubscriptionCatalogLinked>(e).CatalogId));
    }

    [Fact]
    public void Add_without_a_catalog_id_emits_only_added()
    {
        Assert.Single(SubscriptionDecisionTests.Added(Input(null)));
        Assert.Null(SubscriptionState.Rehydrate(SubscriptionDecisionTests.Added(Input(null))).CatalogId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_catalog_id_becomes_null(string blank)
    {
        Assert.Null(SubscriptionDecisions.ParseAdd(Input(blank)).CatalogId);
        Assert.Single(SubscriptionDecisionTests.Added(Input(blank)));
    }

    [Fact]
    public void Catalog_id_is_trimmed()
    {
        Assert.Equal("streamco", SubscriptionDecisions.ParseAdd(Input("  streamco ")).CatalogId);
    }

    [Theory]
    [InlineData("StreamCo")]
    [InlineData("stream_co")]
    [InlineData("-x")]
    [InlineData("x--y")]
    [InlineData("has space")]
    public void Bad_catalog_id_shape_is_rejected_under_catalogId(string id)
    {
        var ex = Assert.Throws<SubscriptionValidationException>(() => SubscriptionDecisions.ParseAdd(Input(id)));
        Assert.Equal("catalogId", Assert.Single(ex.Errors).Key);
    }

    [Fact]
    public void Catalog_id_over_64_characters_is_rejected()
    {
        var ex = Assert.Throws<SubscriptionValidationException>(() => SubscriptionDecisions.ParseAdd(Input(new string('a', 65))));
        Assert.Equal("catalogId", Assert.Single(ex.Errors).Key);
        Assert.Equal(new string('a', 64), SubscriptionDecisions.ParseAdd(Input(new string('a', 64))).CatalogId);
    }

    [Fact]
    public void Update_links_relinks_and_unlinks()
    {
        var history = SubscriptionDecisionTests.Added(Input(null));
        var state = SubscriptionState.Rehydrate(history);

        var link = SubscriptionDecisions.UpdateDetails(history, Update(state, "streamco"), Now);
        Assert.Equal("streamco", Assert.IsType<SubscriptionCatalogLinked>(Assert.Single(link)).CatalogId);
        SubscriptionDecisionTests.Apply(history, link);

        var relink = SubscriptionDecisions.UpdateDetails(history, Update(SubscriptionState.Rehydrate(history), "music-box"), Now);
        Assert.Equal("music-box", Assert.IsType<SubscriptionCatalogLinked>(Assert.Single(relink)).CatalogId);
        SubscriptionDecisionTests.Apply(history, relink);

        var unlink = SubscriptionDecisions.UpdateDetails(history, Update(SubscriptionState.Rehydrate(history), null), Now);
        Assert.Null(Assert.IsType<SubscriptionCatalogLinked>(Assert.Single(unlink)).CatalogId);
        SubscriptionDecisionTests.Apply(history, unlink);
        Assert.Null(SubscriptionState.Rehydrate(history).CatalogId);
    }

    [Fact]
    public void Update_with_an_unchanged_link_emits_nothing()
    {
        var history = Linked();
        Assert.Empty(SubscriptionDecisions.UpdateDetails(history, Update(SubscriptionState.Rehydrate(history), "streamco"), Now));
        var unlinked = SubscriptionDecisionTests.Added(Input(null));
        Assert.Empty(SubscriptionDecisions.UpdateDetails(unlinked, Update(SubscriptionState.Rehydrate(unlinked), null), Now));
    }

    [Fact]
    public void Update_combines_a_detail_change_and_a_link_change()
    {
        var history = Linked();
        var update = Update(SubscriptionState.Rehydrate(history), "music-box") with { Name = "Renamed" };
        var events = SubscriptionDecisions.UpdateDetails(history, update, Now);
        Assert.Collection(events, e => Assert.IsType<SubscriptionDetailsChanged>(e), e => Assert.IsType<SubscriptionCatalogLinked>(e));
    }

    [Fact]
    public void Update_parse_normalizes_the_catalog_id()
    {
        var parsed = SubscriptionDecisions.ParseUpdate(new UpdateInput("StreamCo", null, null, "DKK", "2024-03-01", null, " streamco "));
        Assert.Equal("streamco", parsed.CatalogId);
        Assert.Null(SubscriptionDecisions.ParseUpdate(new UpdateInput("StreamCo", null, null, "DKK", "2024-03-01", null, null)).CatalogId);
        Assert.Throws<SubscriptionValidationException>(() => SubscriptionDecisions.ParseUpdate(new UpdateInput("StreamCo", null, null, "DKK", "2024-03-01", null, "Bad Id")));
    }

    [Fact]
    public void V1_only_history_rehydrates_with_a_null_catalog_id()
    {
        Assert.Null(SubscriptionState.Rehydrate(SubscriptionDecisionTests.FilmPlus()).CatalogId);
    }

    [Fact]
    public void Null_link_round_trips_inline()
    {
        var e = new SubscriptionCatalogLinked(null, Now);
        var json = SubscriptionEventTypes.Serialize(e);
        Assert.Equal(JsonValueKind.Null, JsonElement.Parse(json).GetProperty("catalogId").ValueKind);
        Assert.Equal(e, SubscriptionEventTypes.Deserialize(SubscriptionEventTypes.CatalogLinked, json));
    }

    [Theory]
    [InlineData("subscription.added.v1", "f8b68a39c8c466003dc009d29e0cc9d60c60d71e82bb1758f1648cd90bfa5984")]
    [InlineData("subscription.currency_corrected.v1", "8cca1132701195d58f3fb5c3a80167c903b513235072badeb8b06cf6eac17971")]
    [InlineData("subscription.details_changed.v1", "789e4354cbc7fe51f8163300f8da73fcc5cf475a720b4c668b21899e22062596")]
    [InlineData("subscription.period_changed.v1", "d53d4135a9c0cb19e99f5e19ba2949c5a9c4a0e9dcafff629c1bc84e39616497")]
    [InlineData("subscription.price_entry_added.v1", "1e921f1b5751274e4174b9a0ebcd8fa97079a836dfa3e7c5e96d41b0edba1fdd")]
    [InlineData("subscription.price_entry_corrected.v1", "b5112d434a7445ef867c98f97305ae05d003be5dfdfb69bd4a806f5607b6b66d")]
    [InlineData("subscription.price_entry_removed.v1", "dcc3e806f0e35002626b14026d61369cd7fa0fa15903314a9105488fe96bff7c")]
    [InlineData("subscription.removed.v1", "ea6e247c394f42530aa9c9e5f28e843221d2288e907eb0aa3a2f5903597bce06")]
    public void Existing_v1_fixtures_are_byte_identical_to_master(string alias, string sha256)
    {
        // SHA-256 of each fixture as it was at master 9d2d61e. A change to a v1 shape must be a new alias, never an edit.
        // added and details_changed later gained an optional trailing field: their original payloads are pinned, unchanged, under
        // legacy/, and the top-level fixtures carry the new field.
        var folder = alias is SubscriptionEventTypes.Added or SubscriptionEventTypes.DetailsChanged ? Path.Combine("events", "legacy") : "events";
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", folder, alias + ".json"));
        Assert.Equal(sha256, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)));
    }

    [Fact]
    public void Replaying_every_golden_fixture_still_succeeds()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "events");
        foreach (var alias in SubscriptionEventTypes.ByAlias.Keys)
        {
            var json = File.ReadAllText(Path.Combine(dir, alias + ".json")).Trim();
            Assert.NotNull(SubscriptionEventTypes.Deserialize(alias, json));
        }
    }
}
