using System.Net;
using Subscribed.TestSupport;
using Xunit;

namespace Subscribed.Api.Tests;

public sealed class LogoHostTests(PostgresFixture pg)
{
    [Fact]
    public async Task An_unknown_logo_is_a_404_problem_and_not_the_spa_shell()
    {
        await using var host = await ApiTestHost.StartAsync(pg);
        using var response = await host.Client.GetAsync(new Uri("/logos/missing.svg", UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("<app-root", body, StringComparison.Ordinal);
    }
}
