using System.Globalization;

namespace Subscribed.Platform.Http;

/// <summary>Strong ETags that carry a stream version, quoted as in <c>"3"</c>.</summary>
public static class EntityTags
{
    public static string Format(long version) => $"\"{version.ToString(CultureInfo.InvariantCulture)}\"";

    public static bool TryParse(string? value, out long version)
    {
        version = 0;
        var v = (value ?? "").Trim();
        return v.Length >= 3 && v[0] == '"' && v[^1] == '"'
            && long.TryParse(v.AsSpan(1, v.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out version);
    }
}
