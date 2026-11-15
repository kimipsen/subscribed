using Subscribed.Subscriptions;

namespace Subscribed.TestSupport;

/// <summary>Small in-memory catalogs for tests, so they never depend on the shipped seed data.</summary>
public static class TestCatalog
{
    public const string StreamCo = "streamco";
    public const string MusicBox = "music-box";
    public const string NoLogo = "no-plans";

    public static Catalog Standard() => new(
    [
        new CatalogService(
            StreamCo,
            "StreamCo",
            "Streaming",
            "/logos/streamco.svg",
            new DateOnly(2026, 11, 1),
            "https://example.com/streamco",
            [
                new CatalogPlan("DK", "Family", "DKK", 129.00m, "month", 1, new CatalogUpcomingChange(139.00m, new DateOnly(2027, 2, 1))),
                new CatalogPlan("DK", "Solo", "DKK", 89.00m, "month", 1, null),
            ]),
        new CatalogService(
            MusicBox,
            "Music Box",
            "Music",
            null,
            new DateOnly(2026, 10, 20),
            "https://example.com/music",
            [new CatalogPlan("DK", "Duo", "DKK", 119.00m, "month", 1, null)]),
        new CatalogService(NoLogo, "No Plans", "Other", null, new DateOnly(2026, 10, 1), "https://example.com/none", []),
    ]);

    /// <summary>The same ids minus one, to model an entry removed from the catalog after households linked to it.</summary>
    public static Catalog Without(string id) => new([.. Standard().Services.Where(s => s.Id != id)]);
}
