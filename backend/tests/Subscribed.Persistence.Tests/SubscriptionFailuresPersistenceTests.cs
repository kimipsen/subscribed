using Subscribed.Household;
using Subscribed.Platform.EventStore;
using Subscribed.Subscriptions;
using Subscribed.Subscriptions.Domain;
using Subscribed.TestSupport;
using Xunit;

namespace Subscribed.Persistence.Tests;

/// <summary>The shared failure mapping (#55 WP2): the codes, statuses, field errors and current version the web API answers with.</summary>
public sealed class SubscriptionFailuresPersistenceTests(PostgresFixture pg)
{
    private static readonly IReadOnlyList<IEventStoreModule> Modules = [new Subscribed.Household.HouseholdModule(), new SubscriptionsModule(), new Subscribed.Subscriptions.PaymentOptions.PaymentOptionsModule()];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WriteContext Member => new("member:test-member", "corr-123");

    private static NewSubscription Sub() =>
        SubscriptionDecisions.ParseAdd(new AddInput("StreamCo", null, null, "DKK", "2024-03-01", null, "129.00", "month", 1), TimeZoneInfo.Utc);

    private async Task<(Marten.DocumentStore Store, SubscriptionService Service, SubscriptionState Created, TestDatabase Db)> StartAsync()
    {
        var db = await pg.CreateDatabaseAsync();
        var store = SubscribedStore.Create(db.AppConnectionString, PostgresFixture.Schema, StoreRole.App, Modules);
        var service = new SubscriptionService(store, TimeProvider.System, TestCatalog.Standard());
        var created = await service.AddAsync(Sub(), Member, Ct);
        return (store, service, created, db);
    }

    [Fact]
    public async Task A_missing_subscription_maps_to_404_not_found()
    {
        var (store, service, _, db) = await StartAsync();
        await using var _db = db;
        await using var _store = store;

        var failure = await SubscriptionFailures.MapAsync(new SubscriptionNotFoundException(), "missing", service, Ct);

        Assert.Equal(new SubscriptionFailure(404, "not-found", "Subscription not found.", null, null), failure);
    }

    [Fact]
    public async Task A_validation_failure_maps_to_400_with_the_field_errors()
    {
        var (store, service, _, db) = await StartAsync();
        await using var _db = db;
        await using var _store = store;
        var errors = new Dictionary<string, string[]> { ["name"] = ["Name is required."], ["startDate"] = ["Use a date."] };

        var failure = await SubscriptionFailures.MapAsync(new SubscriptionValidationException(errors), "x", service, Ct);

        Assert.NotNull(failure);
        Assert.Equal(400, failure.Status);
        Assert.Equal("validation-failed", failure.Code);
        Assert.Equal("Validation failed", failure.Title);
        Assert.Equal(errors, failure.Errors);
        Assert.Null(failure.CurrentVersion);
    }

    [Fact]
    public async Task A_state_failure_maps_to_409_with_the_current_version()
    {
        var (store, service, created, db) = await StartAsync();
        await using var _db = db;
        await using var _store = store;
        var bare = SubscriptionIds.Bare(created.Id);

        var thrown = await Assert.ThrowsAsync<SubscriptionStateException>(() => service.ResumeAsync(bare, created.Version, TimeZoneInfo.Utc, Member, Ct));
        var failure = await SubscriptionFailures.MapAsync(thrown, bare, service, Ct);

        Assert.NotNull(failure);
        Assert.Equal(409, failure.Status);
        Assert.Equal("invalid-subscription-state", failure.Code);
        Assert.Null(failure.Errors);
        Assert.Equal(created.Version, failure.CurrentVersion);
    }

    [Fact]
    public async Task A_stale_version_maps_to_412_with_the_current_version()
    {
        var (store, service, created, db) = await StartAsync();
        await using var _db = db;
        await using var _store = store;
        var bare = SubscriptionIds.Bare(created.Id);
        var paused = await service.PauseAsync(bare, created.Version, TimeZoneInfo.Utc, Member, Ct);

        var thrown = await Assert.ThrowsAsync<EventStoreException>(() => service.ResumeAsync(bare, created.Version, TimeZoneInfo.Utc, Member, Ct));
        var failure = await SubscriptionFailures.MapAsync(thrown, bare, service, Ct);

        Assert.NotNull(failure);
        Assert.Equal(412, failure.Status);
        Assert.Equal("concurrency-conflict", failure.Code);
        Assert.Equal("The subscription was changed by someone else.", failure.Title);
        Assert.Equal(paused.Version, failure.CurrentVersion);
    }

    [Fact]
    public async Task An_unclassified_exception_is_not_mapped()
    {
        var (store, service, _, db) = await StartAsync();
        await using var _db = db;
        await using var _store = store;

        Assert.Null(await SubscriptionFailures.MapAsync(new InvalidOperationException("boom"), "x", service, Ct));
    }
}
