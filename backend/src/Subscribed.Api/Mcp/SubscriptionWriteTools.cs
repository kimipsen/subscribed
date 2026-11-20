using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Subscribed.Household;
using Subscribed.Subscriptions;
using Subscribed.Subscriptions.Domain;

namespace Subscribed.Api.Mcp;

/// <summary>
/// The ten write tools (plan WP3 steps 1 and 2). Each one builds the same request record the web endpoint reads, runs the same
/// <see cref="SubscriptionDecisions"/> parse with the household zone, calls the same <see cref="SubscriptionService"/> method with an
/// MCP <see cref="WriteContext"/> and answers with the same detail DTO the web endpoint returns. Every existing-subscription tool takes
/// <c>expectedVersion</c> with the meaning of <c>If-Match</c>. No tool removes a subscription (BR-015).
/// </summary>
[McpServerToolType]
public sealed class SubscriptionWriteTools(
    McpToolRunner runner,
    SubscriptionService subscriptions,
    TimeProvider time,
    Catalog catalog,
    ILoggerFactory loggers)
{
    private const string VersionHelp = "The 'version' returned by get_subscription, right before this change. If the subscription changed since, the call fails with concurrency-conflict and currentVersion; read it again and retry.";
    private const string ConflictHelp = " Errors: validation-failed names the offending fields; invalid-subscription-state and concurrency-conflict carry currentVersion, so re-read with get_subscription and retry.";

    private TimeZoneInfo Zone(McpCaller caller) => HouseholdZones.Resolve(caller.View, loggers);

    private static WriteContext Context(McpCaller caller) => WriteContext.Mcp(caller.MemberId, caller.TokenId, caller.CorrelationId);

    private McpToolResult Detail(SubscriptionState state, McpCaller caller)
    {
        CatalogLog.WarnIfDangling(loggers.CreateLogger(CatalogLog.Category), state, catalog);
        var today = SubscriptionCalculator.Today(time);
        return McpResults.Ok(SubscriptionDetailDto.From(state, today, catalog, Zone(caller), ReportPeriod.CurrentYear(today)));
    }

    /// <summary>
    /// The shared flow of every existing-subscription tool: a malformed id is not-found, then <paramref name="action"/> parses its input
    /// (validation-failed) and calls the service (not-found, concurrency-conflict, invalid-subscription-state); the runner maps the
    /// exceptions with <see cref="SubscriptionFailures"/>.
    /// </summary>
    private Task<CallToolResult> WriteAsync(string tool, string id, Func<string, McpCaller, Task<SubscriptionState>> action, CancellationToken ct)
    {
        var parsed = SubscriptionIds.TryParse(id, out var bare);
        return runner.RunAsync(tool, parsed ? bare : null, async caller =>
            parsed ? Detail(await action(bare, caller).ConfigureAwait(false), caller) : McpResults.Error(SubscriptionFailures.NotFound), ct);
    }

    [McpServerTool(Name = "create_subscription", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Creates a subscription and returns it, including its id and version. This is NOT idempotent: calling it twice creates two subscriptions. If a call fails or times out, call list_subscriptions before retrying to see whether it was created. Amounts are decimal strings (for example \"129.00\"); dates are yyyy-MM-dd; cancelBy is a local wall time yyyy-MM-ddTHH:mm in the household time zone." + ConflictHelp)]
    public Task<CallToolResult> CreateSubscription(
        [Description("The service name.")] string? name = null,
        [Description("Optional category.")] string? category = null,
        [Description("Optional notes.")] string? notes = null,
        [Description("ISO 4217 currency code, for example DKK.")] string? currency = null,
        [Description("First day, yyyy-MM-dd.")] string? startDate = null,
        [Description("Optional last day, yyyy-MM-dd.")] string? endDate = null,
        [Description("The price per billing interval as a decimal string.")] string? amount = null,
        [Description("Billing interval unit: day, week, month or year.")] string? cadenceUnit = null,
        [Description("Number of units per billing interval, at least 1.")] int? cadenceCount = null,
        [Description("Optional catalog id from get_catalog.")] string? catalogId = null,
        [Description("Optional unsubscribe link.")] string? unsubscribeUrl = null,
        [Description("Optional payment option id from list_payment_options.")] string? paymentOptionId = null,
        [Description("Optional cancel-by deadline, yyyy-MM-ddTHH:mm in the household time zone.")] string? cancelBy = null,
        CancellationToken cancellationToken = default) =>
        runner.RunAsync("create_subscription", null, async caller =>
        {
            var request = new AddSubscriptionRequest(name, category, notes, currency, startDate, endDate, amount, cadenceUnit, cadenceCount, catalogId, unsubscribeUrl, paymentOptionId, cancelBy);
            var command = SubscriptionDecisions.ParseAdd(request.ToInput(), Zone(caller));
            var created = await subscriptions.AddAsync(command, Context(caller), cancellationToken).ConfigureAwait(false);
            return Detail(created, caller);
        }, cancellationToken);

    [McpServerTool(Name = "update_subscription", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("This replaces all details. Call get_subscription first and send every field you want to keep; any omitted optional field is cleared. Omitted or null endDate, catalogId, unsubscribeUrl and cancelBy are cleared, and a null endDate reinstates a cancelled subscription. name, currency and startDate are required. paymentOption is the only opt-in field: omit it or send null to leave price entries alone, send { \"id\": null } to clear every payment option, send { \"id\": \"<id>\" } to set one; {} is rejected. Returns the updated subscription." + ConflictHelp)]
    public Task<CallToolResult> UpdateSubscription(
        [Description("The subscription id.")] string id,
        [Description(VersionHelp)] long expectedVersion,
        [Description("The name (required).")] string? name = null,
        [Description("Category; omitted clears it.")] string? category = null,
        [Description("Notes; omitted clears them.")] string? notes = null,
        [Description("ISO 4217 currency code (required).")] string? currency = null,
        [Description("First day, yyyy-MM-dd (required).")] string? startDate = null,
        [Description("Last day, yyyy-MM-dd; omitted or null clears it.")] string? endDate = null,
        [Description("Catalog id; omitted or null unlinks it.")] string? catalogId = null,
        [Description("Unsubscribe link; omitted or null clears it.")] string? unsubscribeUrl = null,
        [Description("{ \"id\": \"<payment option id>\" } sets, { \"id\": null } clears, omitted or null leaves the price entries alone, {} is rejected.")] PaymentOptionRequest? paymentOption = null,
        [Description("Cancel-by deadline, yyyy-MM-ddTHH:mm in the household time zone; omitted or null clears it.")] string? cancelBy = null,
        CancellationToken cancellationToken = default) =>
        WriteAsync("update_subscription", id, (bare, caller) =>
        {
            var request = new UpdateSubscriptionRequest(name, category, notes, currency, startDate, endDate, catalogId, unsubscribeUrl, paymentOption, cancelBy);
            var command = SubscriptionDecisions.ParseUpdate(request.ToInput(), Zone(caller));
            return subscriptions.UpdateAsync(bare, expectedVersion, command, Context(caller), cancellationToken);
        }, cancellationToken);

    [McpServerTool(Name = "add_price_entry", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Adds a price change to a subscription: from effectiveFrom the subscription costs amount per billing interval. Returns the updated subscription." + ConflictHelp)]
    public Task<CallToolResult> AddPriceEntry(
        [Description("The subscription id.")] string id,
        [Description(VersionHelp)] long expectedVersion,
        [Description("First day of the new price, yyyy-MM-dd.")] string? effectiveFrom = null,
        [Description("The price per billing interval as a decimal string.")] string? amount = null,
        [Description("Billing interval unit: day, week, month or year.")] string? cadenceUnit = null,
        [Description("Number of units per billing interval, at least 1.")] int? cadenceCount = null,
        [Description("Optional payment option id; omitted means no payment option.")] string? paymentOptionId = null,
        CancellationToken cancellationToken = default) =>
        WriteAsync("add_price_entry", id, (bare, caller) =>
        {
            var command = SubscriptionDecisions.ParsePriceEntry(new PriceEntryRequest(effectiveFrom, amount, cadenceUnit, cadenceCount, paymentOptionId).ToInput());
            return subscriptions.AddPriceEntryAsync(bare, expectedVersion, command, Context(caller), cancellationToken);
        }, cancellationToken);

    [McpServerTool(Name = "update_price_entry", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Corrects one price entry. This replaces the entry: send every field you want to keep; an omitted paymentOptionId clears the payment option. The entryId comes from get_price_history. Returns the updated subscription." + ConflictHelp)]
    public Task<CallToolResult> UpdatePriceEntry(
        [Description("The subscription id.")] string id,
        [Description(VersionHelp)] long expectedVersion,
        [Description("The price entry id from get_price_history.")] string entryId,
        [Description("First day of the price, yyyy-MM-dd. The initial entry must stay on the start date.")] string? effectiveFrom = null,
        [Description("The price per billing interval as a decimal string.")] string? amount = null,
        [Description("Billing interval unit: day, week, month or year.")] string? cadenceUnit = null,
        [Description("Number of units per billing interval, at least 1.")] int? cadenceCount = null,
        [Description("Payment option id; omitted or null clears it.")] string? paymentOptionId = null,
        CancellationToken cancellationToken = default) =>
        WriteAsync("update_price_entry", id, (bare, caller) =>
        {
            if (!SubscriptionIds.TryParse(entryId, out var entry))
            {
                throw new SubscriptionNotFoundException();
            }

            var command = SubscriptionDecisions.ParsePriceEntry(new PriceEntryRequest(effectiveFrom, amount, cadenceUnit, cadenceCount, paymentOptionId).ToInput());
            return subscriptions.CorrectPriceEntryAsync(bare, expectedVersion, entry, command, Context(caller), cancellationToken);
        }, cancellationToken);

    [McpServerTool(Name = "delete_price_entry", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Deletes one price entry of a subscription. The initial entry cannot be deleted. Returns the updated subscription." + ConflictHelp)]
    public Task<CallToolResult> DeletePriceEntry(
        [Description("The subscription id.")] string id,
        [Description(VersionHelp)] long expectedVersion,
        [Description("The price entry id from get_price_history.")] string entryId,
        CancellationToken cancellationToken = default) =>
        WriteAsync("delete_price_entry", id, (bare, caller) =>
            SubscriptionIds.TryParse(entryId, out var entry)
                ? subscriptions.RemovePriceEntryAsync(bare, expectedVersion, entry, Context(caller), cancellationToken)
                : throw new SubscriptionNotFoundException(), cancellationToken);

    [McpServerTool(Name = "pause_subscription", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Pauses a subscription starting today: it is not charged from today until it is resumed. A subscription that is already paused fails with invalid-subscription-state. Returns the updated subscription." + ConflictHelp)]
    public Task<CallToolResult> PauseSubscription(
        [Description("The subscription id.")] string id,
        [Description(VersionHelp)] long expectedVersion,
        CancellationToken cancellationToken = default) =>
        WriteAsync("pause_subscription", id, (bare, caller) =>
            subscriptions.PauseAsync(bare, expectedVersion, Zone(caller), Context(caller), cancellationToken), cancellationToken);

    [McpServerTool(Name = "resume_subscription", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Resumes a paused subscription from today. A subscription that is not paused fails with invalid-subscription-state. Returns the updated subscription." + ConflictHelp)]
    public Task<CallToolResult> ResumeSubscription(
        [Description("The subscription id.")] string id,
        [Description(VersionHelp)] long expectedVersion,
        CancellationToken cancellationToken = default) =>
        WriteAsync("resume_subscription", id, (bare, caller) =>
            subscriptions.ResumeAsync(bare, expectedVersion, Zone(caller), Context(caller), cancellationToken), cancellationToken);

    [McpServerTool(Name = "add_pause_period", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description("Adds a dated pause period: the subscription is not charged from pauseDate until resumeDate (the first day charged again). Omit resumeDate for an open pause. Periods must not overlap. Returns the updated subscription." + ConflictHelp)]
    public Task<CallToolResult> AddPausePeriod(
        [Description("The subscription id.")] string id,
        [Description(VersionHelp)] long expectedVersion,
        [Description("First day not charged, yyyy-MM-dd.")] string? pauseDate = null,
        [Description("First day charged again, yyyy-MM-dd; omit for an open pause.")] string? resumeDate = null,
        CancellationToken cancellationToken = default) =>
        WriteAsync("add_pause_period", id, (bare, caller) =>
        {
            var command = SubscriptionDecisions.ParsePause(new PauseRequest(pauseDate, resumeDate).ToInput());
            return subscriptions.AddPausePeriodAsync(bare, expectedVersion, command, Zone(caller), Context(caller), cancellationToken);
        }, cancellationToken);

    [McpServerTool(Name = "update_pause_period", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Replaces both dates of one pause period: send pauseDate and resumeDate (omit resumeDate for an open pause). The periodId comes from get_pauses. Returns the updated subscription." + ConflictHelp)]
    public Task<CallToolResult> UpdatePausePeriod(
        [Description("The subscription id.")] string id,
        [Description(VersionHelp)] long expectedVersion,
        [Description("The pause period id from get_pauses.")] string periodId,
        [Description("First day not charged, yyyy-MM-dd.")] string? pauseDate = null,
        [Description("First day charged again, yyyy-MM-dd; omit for an open pause.")] string? resumeDate = null,
        CancellationToken cancellationToken = default) =>
        WriteAsync("update_pause_period", id, (bare, caller) =>
        {
            if (!PausePeriodIds.TryParse(periodId, out var period))
            {
                throw new SubscriptionNotFoundException();
            }

            var command = SubscriptionDecisions.ParsePause(new PauseRequest(pauseDate, resumeDate).ToInput());
            return subscriptions.ChangePausePeriodAsync(bare, expectedVersion, period, command, Zone(caller), Context(caller), cancellationToken);
        }, cancellationToken);

    [McpServerTool(Name = "delete_pause_period", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false)]
    [Description("Deletes one pause period of a subscription. The periodId comes from get_pauses. Returns the updated subscription." + ConflictHelp)]
    public Task<CallToolResult> DeletePausePeriod(
        [Description("The subscription id.")] string id,
        [Description(VersionHelp)] long expectedVersion,
        [Description("The pause period id from get_pauses.")] string periodId,
        CancellationToken cancellationToken = default) =>
        WriteAsync("delete_pause_period", id, (bare, caller) =>
            PausePeriodIds.TryParse(periodId, out var period)
                ? subscriptions.RemovePausePeriodAsync(bare, expectedVersion, period, Zone(caller), Context(caller), cancellationToken)
                : throw new SubscriptionNotFoundException(), cancellationToken);
}
