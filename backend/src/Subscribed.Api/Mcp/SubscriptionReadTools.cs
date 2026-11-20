using System.ComponentModel;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Subscribed.Household;
using Subscribed.Subscriptions;
using Subscribed.Subscriptions.Domain;
using Subscribed.Subscriptions.PaymentOptions;

namespace Subscribed.Api.Mcp;

/// <summary>
/// The ten read tools (plan WP2 step 7). Each returns the same DTO, or a slice of it, as the web endpoint, so an agent sees exactly
/// what the household page sees. All are read-only.
/// </summary>
[McpServerToolType]
public sealed class SubscriptionReadTools(
    McpToolRunner runner,
    SubscriptionService subscriptions,
    PaymentOptionService paymentOptions,
    TimeProvider time,
    Catalog catalog,
    ILoggerFactory loggers)
{
    private const string PeriodHelp = "Report period: year-YYYY (1900-9999), last-12-months or next-12-months. Omit it for the current calendar year.";

    private static (ReportPeriod? Period, McpToolResult? Error) ReadPeriod(string? period, DateOnly today)
    {
        if (period is null)
        {
            return (ReportPeriod.CurrentYear(today), null);
        }

        return ReportPeriod.TryParse(period, today, out var parsed)
            ? (parsed, null)
            : (null, McpResults.Validation("period", "Use year-YYYY (1900-9999), last-12-months or next-12-months."));
    }

    private TimeZoneInfo Zone(McpCaller caller) => HouseholdZones.Resolve(caller.View, loggers);

    [McpServerTool(Name = "list_subscriptions", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Lists every subscription of the household with its current price, status and cost for a report period, plus per-currency totals. Amounts are decimal strings in the subscription's own currency; nothing is converted. Use get_subscription for the history and the version.")]
    public Task<CallToolResult> ListSubscriptions([Description(PeriodHelp)] string? period = null, CancellationToken cancellationToken = default) =>
        runner.RunAsync("list_subscriptions", null, async caller =>
        {
            var today = SubscriptionCalculator.Today(time);
            var (parsed, error) = ReadPeriod(period, today);
            if (error is not null)
            {
                return error;
            }

            var all = await subscriptions.ListAsync(cancellationToken).ConfigureAwait(false);
            CatalogLog.WarnIfAnyDangling(loggers.CreateLogger(CatalogLog.Category), all, catalog);
            return McpResults.Ok(SubscriptionOverviewDto.From(all, today, catalog, Zone(caller), parsed!));
        }, cancellationToken);

    [McpServerTool(Name = "get_subscription", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Returns one subscription: details, current price, full price history, pause history and the report-period total. The returned 'version' is the value every write tool needs as expectedVersion, so read it right before changing anything.")]
    public Task<CallToolResult> GetSubscription([Description("The subscription id, as returned by list_subscriptions.")] string id, [Description(PeriodHelp)] string? period = null, CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_subscription", null, async caller =>
        {
            var today = SubscriptionCalculator.Today(time);
            var (parsed, error) = ReadPeriod(period, today);
            if (error is not null)
            {
                return error;
            }

            if (!SubscriptionIds.TryParse(id, out var bare))
            {
                return McpResults.Error(SubscriptionFailures.NotFound);
            }

            var state = await subscriptions.GetAsync(bare, cancellationToken).ConfigureAwait(false);
            if (state is null)
            {
                return McpResults.Error(SubscriptionFailures.NotFound);
            }

            CatalogLog.WarnIfDangling(loggers.CreateLogger(CatalogLog.Category), state, catalog);
            return McpResults.Ok(SubscriptionDetailDto.From(state, today, catalog, Zone(caller), parsed!));
        }, cancellationToken);

    [McpServerTool(Name = "get_price_history", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Returns the price history of one subscription: one entry per price change, with its effective-from date, amount, billing interval and payment option. It is the priceHistory part of get_subscription.")]
    public Task<CallToolResult> GetPriceHistory([Description("The subscription id.")] string id, CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_price_history", null, async caller =>
        {
            var detail = await DetailAsync(id, caller, cancellationToken).ConfigureAwait(false);
            return detail is null ? McpResults.Error(SubscriptionFailures.NotFound) : McpResults.Ok(detail.PriceHistory);
        }, cancellationToken);

    [McpServerTool(Name = "get_pauses", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Returns the pause periods of one subscription (start, optional end, status). It is the pauseHistory part of get_subscription.")]
    public Task<CallToolResult> GetPauses([Description("The subscription id.")] string id, CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_pauses", null, async caller =>
        {
            var detail = await DetailAsync(id, caller, cancellationToken).ConfigureAwait(false);
            return detail is null ? McpResults.Error(SubscriptionFailures.NotFound) : McpResults.Ok(detail.PauseHistory);
        }, cancellationToken);

    [McpServerTool(Name = "get_totals", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Returns the per-currency totals for a report period: the total, the monthly average and the number of active subscriptions. It is the totals part of list_subscriptions.")]
    public Task<CallToolResult> GetTotals([Description(PeriodHelp)] string? period = null, CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_totals", null, async caller =>
        {
            var today = SubscriptionCalculator.Today(time);
            var (parsed, error) = ReadPeriod(period, today);
            if (error is not null)
            {
                return error;
            }

            var all = await subscriptions.ListAsync(cancellationToken).ConfigureAwait(false);
            return McpResults.Ok(SubscriptionOverviewDto.From(all, today, catalog, Zone(caller), parsed!).Totals);
        }, cancellationToken);

    [McpServerTool(Name = "list_periods", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Lists the report periods that make sense for this household (calendar years with data, last-12-months, next-12-months), as accepted by the period argument of the other tools.")]
    public async Task<CallToolResult> ListPeriods(CancellationToken cancellationToken = default) =>
        await runner.RunAsync("list_periods", null, async _ =>
        {
            var all = await subscriptions.ListAsync(cancellationToken).ConfigureAwait(false);
            return McpResults.Ok(PeriodOptionsDto.From(all, SubscriptionCalculator.Today(time)));
        }, cancellationToken).ConfigureAwait(false);

    [McpServerTool(Name = "list_payment_options", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Lists the household's payment options (cards, accounts). A removed option stays in the list with removed=true so older price entries keep resolving.")]
    public async Task<CallToolResult> ListPaymentOptions(CancellationToken cancellationToken = default) =>
        await runner.RunAsync("list_payment_options", null, async _ =>
        {
            var options = await paymentOptions.ListAsync(cancellationToken).ConfigureAwait(false);
            return McpResults.Ok(new PaymentOptionListDto([.. options.Select(PaymentOptionDto.From)]));
        }, cancellationToken).ConfigureAwait(false);

    [McpServerTool(Name = "get_payment_option_suggestions", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Returns the suggested names for new payment options.")]
    public Task<CallToolResult> GetPaymentOptionSuggestions(CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_payment_option_suggestions", null, _ => Task.FromResult(McpResults.Ok(PaymentOptionSuggestionsDto.Default)), cancellationToken);

    [McpServerTool(Name = "get_catalog", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Returns the built-in catalog of well-known subscription services (id, name, category, official links) that a subscription can be linked to.")]
    public Task<CallToolResult> GetCatalog(CancellationToken cancellationToken = default) =>
        runner.RunAsync("get_catalog", null, _ => Task.FromResult(McpResults.Ok(CatalogDto.From(catalog))), cancellationToken);

    [McpServerTool(Name = "get_subscription_options", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Returns the currencies and other choices subscriptions can use in this household.")]
    public async Task<CallToolResult> GetSubscriptionOptions(CancellationToken cancellationToken = default) =>
        await runner.RunAsync("get_subscription_options", null, async _ =>
        {
            var all = await subscriptions.ListAsync(cancellationToken).ConfigureAwait(false);
            return McpResults.Ok(SubscriptionOptionsDto.From(all));
        }, cancellationToken).ConfigureAwait(false);

    private async Task<SubscriptionDetailDto?> DetailAsync(string id, McpCaller caller, CancellationToken ct)
    {
        if (!SubscriptionIds.TryParse(id, out var bare))
        {
            return null;
        }

        var state = await subscriptions.GetAsync(bare, ct).ConfigureAwait(false);
        var today = SubscriptionCalculator.Today(time);
        return state is null ? null : SubscriptionDetailDto.From(state, today, catalog, Zone(caller), ReportPeriod.CurrentYear(today));
    }
}
