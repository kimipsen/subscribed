namespace Subscribed.UnitTests;

/// <summary>Zones for tests that call the zone-aware parse methods. Named so the zone is visible at the call site.</summary>
internal static class TestZones
{
    public static TimeZoneInfo Utc => TimeZoneInfo.Utc;

    public static TimeZoneInfo Copenhagen { get; } = TimeZoneInfo.FindSystemTimeZoneById("Europe/Copenhagen");

    public static TimeZoneInfo Auckland { get; } = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland");
}
