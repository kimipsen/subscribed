using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Subscribed.Household;
using Subscribed.Platform.EventStore;
using Subscribed.Platform.Http;
using Subscribed.Platform.Identity;
using Subscribed.Platform.Problems;
using Subscribed.Subscriptions.Domain;

namespace Subscribed.Subscriptions;

public static class SubscriptionEndpoints
{
    /// <summary>
    /// Request bodies are read with their own options, never with the event options: numbers are never read from JSON strings,
    /// and unknown properties are ignored. Property names are camelCase, matched case-insensitively.
    /// </summary>
    internal static readonly JsonSerializerOptions RequestJson = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.Strict,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public static IEndpointRouteBuilder MapSubscriptionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup("/api/subscriptions").RequireAuthorization(HouseholdMembership.PolicyName);
        group.MapGet("", OverviewAsync);
        group.MapGet("options", OptionsAsync);
        group.MapGet("catalog", CatalogAsync);
        group.MapGet("periods", PeriodsAsync);
        group.MapPost("", AddAsync);
        group.MapGet("{id}", GetAsync);
        group.MapPut("{id}", UpdateAsync);
        group.MapDelete("{id}", RemoveAsync);
        group.MapPost("{id}/prices", AddPriceAsync);
        group.MapPut("{id}/prices/{entryId}", CorrectPriceAsync);
        group.MapDelete("{id}/prices/{entryId}", RemovePriceAsync);
        group.MapPost("{id}/pauses", AddPauseAsync);
        group.MapPut("{id}/pauses/{periodId}", ChangePauseAsync);
        group.MapDelete("{id}/pauses/{periodId}", RemovePauseAsync);
        group.MapPost("{id}/pause", PauseAsync);
        group.MapPost("{id}/resume", ResumeAsync);
        return endpoints;
    }

    private static async Task<IResult> OverviewAsync(HttpContext context, SubscriptionService service, TimeProvider time, Catalog catalog, ILoggerFactory loggers)
    {
        var today = SubscriptionCalculator.Today(time);
        if (ReadPeriod(context, today, out var period) is { } problem)
        {
            return problem;
        }

        var subscriptions = await service.ListAsync(context.RequestAborted).ConfigureAwait(false);
        CatalogLog.WarnIfAnyDangling(loggers.CreateLogger(CatalogLog.Category), subscriptions, catalog);
        return Json(context, SubscriptionOverviewDto.From(subscriptions, today, catalog, HouseholdZone(context, loggers), period));
    }

    /// <summary>What the period selector offers (D3). A literal route, so it wins over <c>{id}</c>.</summary>
    private static async Task<IResult> PeriodsAsync(HttpContext context, SubscriptionService service, TimeProvider time)
    {
        var subscriptions = await service.ListAsync(context.RequestAborted).ConfigureAwait(false);
        return Json(context, PeriodOptionsDto.From(subscriptions, SubscriptionCalculator.Today(time)));
    }

    private static async Task<IResult> OptionsAsync(HttpContext context, SubscriptionService service)
    {
        var subscriptions = await service.ListAsync(context.RequestAborted).ConfigureAwait(false);
        return Json(context, SubscriptionOptionsDto.From(subscriptions));
    }

    private static IResult CatalogAsync(HttpContext context, Catalog catalog) => Json(context, CatalogDto.From(catalog));

    private static async Task<IResult> GetAsync(string id, HttpContext context, SubscriptionService service, TimeProvider time, Catalog catalog, ILoggerFactory loggers)
    {
        if (!SubscriptionIds.TryParse(id, out var bare))
        {
            return NotFound();
        }

        if (ReadPeriod(context, SubscriptionCalculator.Today(time), out _) is { } problem)
        {
            return problem;
        }

        var state = await service.GetAsync(bare, context.RequestAborted).ConfigureAwait(false);
        return state is null ? NotFound() : Detail(context, state, time, catalog, loggers, StatusCodes.Status200OK);
    }

    private static async Task<IResult> AddAsync(HttpContext context, SubscriptionService service, TimeProvider time, Catalog catalog, ILoggerFactory loggers)
    {
        var body = await JsonRequest.ReadAsync<AddSubscriptionRequest>(context, RequestJson).ConfigureAwait(false);
        if (body.Error is { } error)
        {
            return error;
        }

        NewSubscription command;
        try
        {
            command = SubscriptionDecisions.ParseAdd(body.Value!.ToInput(), HouseholdZone(context, loggers));
        }
        catch (SubscriptionValidationException ex)
        {
            return Invalid(ex);
        }

        var memberId = HouseholdMembership.MemberIdFor(context);
        try
        {
            var created = await service.AddAsync(command, WriteContext.Member(memberId, Problem.TraceId(context)), context.RequestAborted).ConfigureAwait(false);
            context.Response.Headers.Location = "/api/subscriptions/" + SubscriptionIds.Bare(created.Id);
            return Detail(context, created, time, catalog, loggers, StatusCodes.Status201Created);
        }
        catch (SubscriptionValidationException ex)
        {
            return Invalid(ex);
        }
    }

    /// <summary>
    /// Details plus the optional <c>paymentOption</c> in one request: the generic <see cref="MutateAsync{TRequest, TCommand}"/> carries the
    /// whole body, and the service turns it into one decision and one append.
    /// </summary>
    private static Task<IResult> UpdateAsync(string id, HttpContext context, SubscriptionService service, TimeProvider time, Catalog catalog, ILoggerFactory loggers) =>
        MutateAsync<UpdateSubscriptionRequest, DetailsUpdate>(id, context, service, loggers,
            request => SubscriptionDecisions.ParseUpdate(request.ToInput(), HouseholdZone(context, loggers)),
            (bare, version, command, write) => service.UpdateAsync(bare, version, command, write, context.RequestAborted),
            state => Detail(context, state, time, catalog, loggers, StatusCodes.Status200OK));

    private static Task<IResult> AddPriceAsync(string id, HttpContext context, SubscriptionService service, TimeProvider time, Catalog catalog, ILoggerFactory loggers) =>
        MutateAsync<PriceEntryRequest, PriceEntryChange>(id, context, service, loggers,
            request => SubscriptionDecisions.ParsePriceEntry(request.ToInput()),
            (bare, version, command, write) => service.AddPriceEntryAsync(bare, version, command, write, context.RequestAborted),
            state => Detail(context, state, time, catalog, loggers, StatusCodes.Status200OK));

    private static Task<IResult> CorrectPriceAsync(string id, string entryId, HttpContext context, SubscriptionService service, TimeProvider time, Catalog catalog, ILoggerFactory loggers) =>
        SubscriptionIds.TryParse(entryId, out var entry)
            ? MutateAsync<PriceEntryRequest, PriceEntryChange>(id, context, service, loggers,
                request => SubscriptionDecisions.ParsePriceEntry(request.ToInput()),
                (bare, version, command, write) => service.CorrectPriceEntryAsync(bare, version, entry, command, write, context.RequestAborted),
                state => Detail(context, state, time, catalog, loggers, StatusCodes.Status200OK))
            : Task.FromResult(NotFound());

    private static Task<IResult> RemovePriceAsync(string id, string entryId, HttpContext context, SubscriptionService service, TimeProvider time, Catalog catalog, ILoggerFactory loggers) =>
        SubscriptionIds.TryParse(entryId, out var entry)
            ? MutateNoBodyAsync(id, context, service, loggers,
                (bare, version, write) => service.RemovePriceEntryAsync(bare, version, entry, write, context.RequestAborted),
                state => Detail(context, state, time, catalog, loggers, StatusCodes.Status200OK))
            : Task.FromResult(NotFound());

    private static Task<IResult> AddPauseAsync(string id, HttpContext context, SubscriptionService service, TimeProvider time, Catalog catalog, ILoggerFactory loggers) =>
        MutateAsync<PauseRequest, PauseChange>(id, context, service, loggers,
            request => SubscriptionDecisions.ParsePause(request.ToInput()),
            (bare, version, command, write) => service.AddPausePeriodAsync(bare, version, command, HouseholdZone(context, loggers), write, context.RequestAborted),
            state => Detail(context, state, time, catalog, loggers, StatusCodes.Status200OK));

    private static Task<IResult> ChangePauseAsync(string id, string periodId, HttpContext context, SubscriptionService service, TimeProvider time, Catalog catalog, ILoggerFactory loggers) =>
        PausePeriodIds.TryParse(periodId, out var period)
            ? MutateAsync<PauseRequest, PauseChange>(id, context, service, loggers,
                request => SubscriptionDecisions.ParsePause(request.ToInput()),
                (bare, version, command, write) => service.ChangePausePeriodAsync(bare, version, period, command, HouseholdZone(context, loggers), write, context.RequestAborted),
                state => Detail(context, state, time, catalog, loggers, StatusCodes.Status200OK))
            : Task.FromResult(NotFound());

    private static Task<IResult> RemovePauseAsync(string id, string periodId, HttpContext context, SubscriptionService service, TimeProvider time, Catalog catalog, ILoggerFactory loggers) =>
        PausePeriodIds.TryParse(periodId, out var period)
            ? MutateNoBodyAsync(id, context, service, loggers,
                (bare, version, write) => service.RemovePausePeriodAsync(bare, version, period, HouseholdZone(context, loggers), write, context.RequestAborted),
                state => Detail(context, state, time, catalog, loggers, StatusCodes.Status200OK))
            : Task.FromResult(NotFound());

    private static Task<IResult> PauseAsync(string id, HttpContext context, SubscriptionService service, TimeProvider time, Catalog catalog, ILoggerFactory loggers) =>
        MutateNoBodyAsync(id, context, service, loggers,
            (bare, version, write) => service.PauseAsync(bare, version, HouseholdZone(context, loggers), write, context.RequestAborted),
            state => Detail(context, state, time, catalog, loggers, StatusCodes.Status200OK));

    private static Task<IResult> ResumeAsync(string id, HttpContext context, SubscriptionService service, TimeProvider time, Catalog catalog, ILoggerFactory loggers) =>
        MutateNoBodyAsync(id, context, service, loggers,
            (bare, version, write) => service.ResumeAsync(bare, version, HouseholdZone(context, loggers), write, context.RequestAborted),
            state => Detail(context, state, time, catalog, loggers, StatusCodes.Status200OK));

    private static Task<IResult> RemoveAsync(string id, HttpContext context, SubscriptionService service, ILoggerFactory loggers) =>
        MutateNoBodyAsync(id, context, service, loggers,
            (bare, version, write) => service.RemoveAsync(bare, version, write, context.RequestAborted),
            _ =>
            {
                context.Response.Headers.CacheControl = "no-store";
                return Results.NoContent();
            });

    /// <summary>
    /// Mutating requests are processed in this order: 1 id format (404), 2 If-Match (428 / 400), 3 body (413, 415, 400), 4 field
    /// shape (400), then the service: 5 load (404), 6 stale version (412), 7 invariants (400 validation, 409 state), 8 append (412 on a race).
    /// </summary>
    private static async Task<IResult> MutateAsync<TRequest, TCommand>(
        string id,
        HttpContext context,
        SubscriptionService service,
        ILoggerFactory loggers,
        Func<TRequest, TCommand> parse,
        Func<string, long, TCommand, WriteContext, Task<SubscriptionState>> write,
        Func<SubscriptionState, IResult> respond)
        where TRequest : class
    {
        if (!SubscriptionIds.TryParse(id, out var bare))
        {
            return NotFound();
        }

        if (!IfMatch.TryRead(context, out var version, out var problem))
        {
            return problem!;
        }

        var body = await JsonRequest.ReadAsync<TRequest>(context, RequestJson).ConfigureAwait(false);
        if (body.Error is { } error)
        {
            return error;
        }

        TCommand command;
        try
        {
            command = parse(body.Value!);
        }
        catch (SubscriptionValidationException ex)
        {
            return Invalid(ex);
        }

        return await GuardedAsync(bare, context, service, loggers, memberId => write(bare, version, command, WriteContext.Member(memberId, Problem.TraceId(context))), respond).ConfigureAwait(false);
    }

    private static async Task<IResult> MutateNoBodyAsync(
        string id,
        HttpContext context,
        SubscriptionService service,
        ILoggerFactory loggers,
        Func<string, long, WriteContext, Task<SubscriptionState>> write,
        Func<SubscriptionState, IResult> respond)
    {
        if (!SubscriptionIds.TryParse(id, out var bare))
        {
            return NotFound();
        }

        if (!IfMatch.TryRead(context, out var version, out var problem))
        {
            return problem!;
        }

        return await GuardedAsync(bare, context, service, loggers, memberId => write(bare, version, WriteContext.Member(memberId, Problem.TraceId(context))), respond).ConfigureAwait(false);
    }

    private static async Task<IResult> GuardedAsync(
        string bare,
        HttpContext context,
        SubscriptionService service,
        ILoggerFactory loggers,
        Func<string, Task<SubscriptionState>> action,
        Func<SubscriptionState, IResult> respond)
    {
        var memberId = HouseholdMembership.MemberIdFor(context);
        try
        {
            return respond(await action(memberId).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is SubscriptionNotFoundException or SubscriptionValidationException or SubscriptionStateException
            || ex is EventStoreException { Failure.Kind: EventStoreFailureKind.ConcurrencyConflict })
        {
            if (ex is EventStoreException)
            {
                SecurityLog.Info(loggers.CreateLogger(SecurityEvents.Category), SecurityEvents.ConcurrencyConflict, memberId);
            }

            var failure = await SubscriptionFailures.MapAsync(ex, bare, service, context.RequestAborted).ConfigureAwait(false);
            return SubscriptionFailures.ToResult(failure!);
        }
    }

    /// <summary>
    /// The detail for the requested period. A GET validates <c>?period=</c> before it gets here, so an unparseable value can only
    /// reach this point after a mutation: the write already happened and must not be reported as failed, so the response falls
    /// back to the current year (D1).
    /// </summary>
    private static IResult Detail(HttpContext context, SubscriptionState state, TimeProvider time, Catalog catalog, ILoggerFactory loggers, int status)
    {
        CatalogLog.WarnIfDangling(loggers.CreateLogger(CatalogLog.Category), state, catalog);
        var today = SubscriptionCalculator.Today(time);
        _ = ReadPeriod(context, today, out var period);
        context.Response.Headers.ETag = EntityTags.Format(state.Version);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(SubscriptionDetailDto.From(state, today, catalog, HouseholdZone(context, loggers), period), statusCode: status);
    }

    /// <summary>
    /// Reads <c>?period=</c>: absent means the current calendar year. An unparseable or repeated value returns a 400 with a
    /// <c>period</c> field error; <paramref name="period"/> is then the current year, which a lenient caller may use (D1).
    /// </summary>
    private static IResult? ReadPeriod(HttpContext context, DateOnly today, out ReportPeriod period)
    {
        var raw = context.Request.Query["period"];
        if (raw.Count == 0)
        {
            period = ReportPeriod.CurrentYear(today);
            return null;
        }

        if (raw.Count == 1 && ReportPeriod.TryParse(raw[0], today, out period))
        {
            return null;
        }

        period = ReportPeriod.CurrentYear(today);
        return ValidationProblems.Result(new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["period"] = ["Use year-YYYY (1900-9999), last-12-months or next-12-months."],
        });
    }

    private static IResult Json<T>(HttpContext context, T value)
    {
        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(value);
    }

    private const string ZoneKey = "subscribed.subscriptions.zone";

    /// <summary>
    /// The household zone for this request, resolved once and cached on the request, so the parsed instant, the compared date and
    /// the DTO rendering all use the same instance. A stored id this server cannot resolve falls back to UTC with a warning that
    /// carries only the household id, never the zone id string or any request input.
    /// </summary>
    private static TimeZoneInfo HouseholdZone(HttpContext context, ILoggerFactory loggers)
    {
        if (context.Items[ZoneKey] is TimeZoneInfo cached)
        {
            return cached;
        }

        var resolved = HouseholdZones.Resolve(HouseholdMembership.ViewFor(context), loggers);
        context.Items[ZoneKey] = resolved;
        return resolved;
    }

    private static IResult NotFound() => SubscriptionFailures.ToResult(SubscriptionFailures.NotFound);

    private static IResult Invalid(SubscriptionValidationException ex) => SubscriptionFailures.ToResult(SubscriptionFailures.Validation(ex));
}
