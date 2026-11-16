using Subscribed.Subscriptions;
using Xunit;

namespace Subscribed.UnitTests;

public sealed class LogoLicenceTests
{
    private static string LogosDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Subscribed.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.GetFullPath(Path.Combine(dir.FullName, "..", "frontend", "public", "logos"));
    }

    [Fact]
    public void Every_shipped_logo_file_has_a_row_in_LICENSES_md()
    {
        var directory = LogosDirectory();
        var licences = File.ReadAllText(Path.Combine(directory, "LICENSES.md"));
        var rows = licences.Split('\n').Where(static l => l.TrimStart().StartsWith('|')).ToList();
        foreach (var file in Directory.GetFiles(directory).Select(Path.GetFileName).Where(static n => n != "LICENSES.md"))
        {
            Assert.True(rows.Any(r => r.Contains($"| {file} |", StringComparison.Ordinal)), $"{file} has no row in frontend/public/logos/LICENSES.md.");
        }
    }

    [Fact]
    public void Every_logo_path_in_the_catalog_exists_on_disk()
    {
        var directory = LogosDirectory();
        var catalog = CatalogLoader.LoadEmbedded(TimeProvider.System);
        foreach (var logo in catalog.Services.Select(static s => s.Logo).OfType<string>())
        {
            Assert.True(File.Exists(Path.Combine(directory, Path.GetFileName(logo))), $"Catalog logo {logo} is missing from frontend/public/logos.");
        }
    }
}
