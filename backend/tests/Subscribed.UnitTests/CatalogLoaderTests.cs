using System.Text;
using Subscribed.Subscriptions;
using Xunit;

namespace Subscribed.UnitTests;

public sealed class CatalogLoaderTests
{
    private static readonly DateOnly Today = new(2026, 11, 15);

    private const string Valid = """
        { "schemaVersion": 1,
          "services": [
            { "id": "streamco", "name": "StreamCo", "category": "Streaming", "logo": null,
              "lastVerified": "2026-11-14", "sourceUrl": "https://example.com/pricing",
              "plans": [
                { "country": "DK", "name": "Family", "currency": "DKK", "amount": "129.00",
                  "cadenceUnit": "month", "cadenceCount": 1,
                  "upcomingChange": { "amount": "139.00", "effectiveFrom": "2027-02-01" } },
                { "country": "DK", "name": "Solo", "currency": "DKK", "amount": "89", "cadenceUnit": "month", "cadenceCount": 1 } ] },
            { "id": "cloud-box", "name": "Cloud Box", "category": "Storage", "logo": "/logos/cloud-box.svg",
              "lastVerified": "2026-11-01", "sourceUrl": "https://example.com/cloud", "plans": [] } ] }
        """;

    private static Catalog Load(string json, DateOnly? today = null) =>
        CatalogLoader.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)), today ?? Today);

    private static CatalogValidationException Fails(string json, DateOnly? today = null) =>
        Assert.Throws<CatalogValidationException>(() => Load(json, today));

    private static string Change(string from, string to)
    {
        Assert.Contains(from, Valid, StringComparison.Ordinal);
        return Valid.Replace(from, to, StringComparison.Ordinal);
    }

    [Fact]
    public void Valid_sample_loads_and_keeps_file_order()
    {
        var catalog = Load(Valid);
        Assert.Equal(["streamco", "cloud-box"], catalog.Services.Select(s => s.Id));
        var service = catalog.TryGet("streamco")!;
        Assert.Equal(["Family", "Solo"], service.Plans.Select(p => p.Name));
        Assert.Equal(129.00m, service.Plans[0].Amount);
        Assert.Equal(new DateOnly(2027, 2, 1), service.Plans[0].UpcomingChange!.EffectiveFrom);
        Assert.Null(service.Plans[1].UpcomingChange);
        Assert.Equal("/logos/cloud-box.svg", catalog.TryGet("cloud-box")!.Logo);
        Assert.Empty(catalog.TryGet("cloud-box")!.Plans);
        Assert.Equal("DK", catalog.DefaultCountry);
        Assert.Equal(new DateOnly(2026, 11, 1), catalog.OldestVerified);
    }

    [Fact]
    public void TryGet_returns_null_for_unknown_or_null_ids()
    {
        var catalog = Load(Valid);
        Assert.Null(catalog.TryGet("nope"));
        Assert.Null(catalog.TryGet(null));
        Assert.Null(Catalog.Empty.TryGet("streamco"));
    }

    [Fact]
    public void Duplicate_id_names_the_service_and_field()
    {
        var ex = Fails(Change("\"id\": \"cloud-box\"", "\"id\": \"streamco\""));
        Assert.Contains("'streamco'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'id'", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Streamco")]
    [InlineData("stream_co")]
    [InlineData("-streamco")]
    [InlineData("streamco-")]
    [InlineData("stream--co")]
    public void Bad_id_shape_is_rejected(string id)
    {
        var ex = Fails(Change("\"id\": \"streamco\"", $"\"id\": \"{id}\""));
        Assert.Contains("'id'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Id_longer_than_64_characters_is_rejected()
    {
        var ex = Fails(Change("\"id\": \"streamco\"", $"\"id\": \"{new string('a', 65)}\""));
        Assert.Contains("'id'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bad_currency_is_rejected()
    {
        var ex = Fails(Change("\"currency\": \"DKK\", \"amount\": \"129.00\"", "\"currency\": \"XXX\", \"amount\": \"129.00\""));
        Assert.Contains("'streamco'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("currency", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0.00")]
    [InlineData("0")]
    [InlineData("-5.00")]
    [InlineData("abc")]
    public void Zero_negative_or_unparseable_amount_is_rejected(string amount)
    {
        var ex = Fails(Change("\"amount\": \"129.00\"", $"\"amount\": \"{amount}\""));
        Assert.Contains("amount", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Amount_with_too_many_decimals_for_the_currency_is_rejected()
    {
        var ex = Fails(Change("\"currency\": \"DKK\", \"amount\": \"129.00\"", "\"currency\": \"JPY\", \"amount\": \"129.50\""));
        Assert.Contains("amount", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Upcoming_change_amount_is_checked_against_the_plan_currency()
    {
        var ex = Fails(Change("\"amount\": \"139.00\"", "\"amount\": \"139.001\""));
        Assert.Contains("upcomingChange.amount", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bad_dates_are_rejected()
    {
        Assert.Contains("lastVerified", Fails(Change("\"lastVerified\": \"2026-11-14\"", "\"lastVerified\": \"2026-02-30\"")).Message, StringComparison.Ordinal);
        Assert.Contains("effectiveFrom", Fails(Change("\"effectiveFrom\": \"2027-02-01\"", "\"effectiveFrom\": \"soon\"")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Last_verified_allows_one_day_of_clock_skew_but_not_two()
    {
        var tomorrow = Today.AddDays(1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var dayAfter = Today.AddDays(2).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        Assert.NotNull(Load(Change("\"lastVerified\": \"2026-11-14\"", $"\"lastVerified\": \"{tomorrow}\"")));
        var ex = Fails(Change("\"lastVerified\": \"2026-11-14\"", $"\"lastVerified\": \"{dayAfter}\""));
        Assert.Contains("'streamco'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("lastVerified", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Past_upcoming_change_is_valid()
    {
        var catalog = Load(Change("\"effectiveFrom\": \"2027-02-01\"", "\"effectiveFrom\": \"2020-01-01\""));
        Assert.Equal(new DateOnly(2020, 1, 1), catalog.TryGet("streamco")!.Plans[0].UpcomingChange!.EffectiveFrom);
    }

    [Theory]
    [InlineData("logos/x.svg")]
    [InlineData("/logos/X.svg")]
    [InlineData("/logos/x.gif")]
    [InlineData("/other/x.svg")]
    [InlineData("https://example.com/x.svg")]
    [InlineData("/logos/../x.svg")]
    public void Bad_logo_path_is_rejected(string logo)
    {
        var ex = Fails(Change("\"logo\": \"/logos/cloud-box.svg\"", $"\"logo\": \"{logo}\""));
        Assert.Contains("'cloud-box'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("logo", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://example.com/pricing")]
    [InlineData("/pricing")]
    [InlineData("not a url")]
    public void Source_url_must_be_absolute_https(string url)
    {
        var ex = Fails(Change("https://example.com/pricing", url));
        Assert.Contains("sourceUrl", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("dk")]
    [InlineData("DNK")]
    [InlineData("D1")]
    public void Country_must_be_two_uppercase_letters(string country)
    {
        var ex = Fails(Change("\"country\": \"DK\", \"name\": \"Family\"", $"\"country\": \"{country}\", \"name\": \"Family\""));
        Assert.Contains("country", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Bad_cadence_is_rejected()
    {
        Assert.Contains("cadence", Fails(Change("\"cadenceUnit\": \"month\", \"cadenceCount\": 1,\n", "\"cadenceUnit\": \"decade\", \"cadenceCount\": 1,\n")).Message, StringComparison.Ordinal);
        Assert.Contains("cadence", Fails(Change("\"cadenceUnit\": \"month\", \"cadenceCount\": 1 }", "\"cadenceUnit\": \"month\", \"cadenceCount\": 0 }")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Name_and_category_limits_apply()
    {
        Assert.Contains("'name'", Fails(Change("\"name\": \"StreamCo\"", $"\"name\": \"{new string('a', 81)}\"")).Message, StringComparison.Ordinal);
        Assert.Contains("'category'", Fails(Change("\"category\": \"Streaming\"", $"\"category\": \"{new string('a', 41)}\"")).Message, StringComparison.Ordinal);
        Assert.Contains("'category'", Fails(Change("\"category\": \"Streaming\"", "\"category\": \"\"")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_schema_version_is_rejected()
    {
        Assert.Contains("schemaVersion", Fails(Change("\"schemaVersion\": 1", "\"schemaVersion\": 2")).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{ \"schemaVersion\": 1 }")]
    public void Malformed_json_is_rejected(string json)
    {
        Assert.Throws<CatalogValidationException>(() => Load(json));
    }

    [Fact]
    public void Shipped_catalog_loads()
    {
        var catalog = CatalogLoader.LoadEmbedded(TimeProvider.System);
        Assert.NotNull(catalog);
    }
}
