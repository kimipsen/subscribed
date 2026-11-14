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
        group.MapPost("", AddAsync);
        group.MapGet("{id}", GetAsync);
        group.MapPut("{id}", UpdateAsync);
        group.MapDelete("{id}", RemoveAsync);
        group.MapPost("{id}/prices", AddPriceAsync);
        group.MapPut("{id}/prices/{entryId}", CorrectPriceAsync);
        group.MapDelete("{id}/prices/{entryId}", RemovePriceAsync);
        return endpoints;
    }

    private static async Task<IResult> OverviewAsync(HttpContext context, SubscriptionService service, TimeProvider time)
    {
        var subscriptions = await service.ListAsync(context.RequestAborted).ConfigureAwait(false);
        return Json(context, SubscriptionOverviewDto.From(subscriptions, SubscriptionCalculator.Today(time)));
    }

    private static async Task<IResult> OptionsAsync(HttpContext context, SubscriptionService service)
    {
        var subscriptions = await service.ListAsync(context.RequestAborted).ConfigureAwait(false);
        return Json(context, SubscriptionOptionsDto.From(subscriptions));
    }

    private static async Task<IResult> GetAsync(string id, HttpContext context, SubscriptionService service, TimeProvider time)
    {
        if (!SubscriptionIds.TryParse(id, out var bare))
        {
            return NotFound();
        }

        var state = await service.GetAsync(bare, context.RequestAborted).ConfigureAwait(false);
        return state is null ? NotFound() : Detail(context, state, time, StatusCodes.Status200OK);
    }

    private static async Task<IResult> AddAsync(HttpContext context, SubscriptionService service, TimeProvider time)
    {
        var body = await JsonRequest.ReadAsync<AddSubscriptionRequest>(context, RequestJson).ConfigureAwait(false);
        if (body.Error is { } error)
        {
            return error;
        }

        NewSubscription command;
        try
        {
            command = SubscriptionDecisions.ParseAdd(body.Value!.ToInput());
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
            return Detail(context, created, time, StatusCodes.Status201Created);
        }
        catch (SubscriptionValidationException ex)
        {
            return Invalid(ex);
        }
    }

    private static Task<IResult> UpdateAsync(string id, HttpContext context, SubscriptionService service, TimeProvider time, ILoggerFactory loggers) =>
        MutateAsync<UpdateSubscriptionRequest, DetailsUpdate>(id, context, service, loggers,
            request => SubscriptionDecisions.ParseUpdate(request.ToInput()),
            (bare, version, command, write) => service.UpdateAsync(bare, version, command, write, context.RequestAborted),
            state => Detail(context, state, time, StatusCodes.Status200OK));

    private static Task<IResult> AddPriceAsync(string id, HttpContext context, SubscriptionService service, TimeProvider time, ILoggerFactory loggers) =>
        MutateAsync<PriceEntryRequest, PriceEntryChange>(id, context, service, loggers,
            request => SubscriptionDecisions.ParsePriceEntry(request.ToInput()),
            (bare, version, command, write) => service.AddPriceEntryAsync(bare, version, command, write, context.RequestAborted),
            state => Detail(context, state, time, StatusCodes.Status200OK));

    private static Task<IResult> CorrectPriceAsync(string id, string entryId, HttpContext context, SubscriptionService service, TimeProvider time, ILoggerFactory loggers) =>
        SubscriptionIds.TryParse(entryId, out var entry)
            ? MutateAsync<PriceEntryRequest, PriceEntryChange>(id, context, service, loggers,
                request => SubscriptionDecisions.ParsePriceEntry(request.ToInput()),
                (bare, version, command, write) => service.CorrectPriceEntryAsync(bare, version, entry, command, write, context.RequestAborted),
                state => Detail(context, state, time, StatusCodes.Status200OK))
            : Task.FromResult(NotFound());

    private static Task<IResult> RemovePriceAsync(string id, string entryId, HttpContext context, SubscriptionService service, TimeProvider time, ILoggerFactory loggers) =>
        SubscriptionIds.TryParse(entryId, out var entry)
            ? MutateNoBodyAsync(id, context, service, loggers,
                (bare, version, write) => service.RemovePriceEntryAsync(bare, version, entry, write, context.RequestAborted),
                state => Detail(context, state, time, StatusCodes.Status200OK))
            : Task.FromResult(NotFound());

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
    /// shape (400), then the service: 5 load (404), 6 stale version (412), 7 invariants (400), 8 append (412 on a race).
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
        catch (SubscriptionNotFoundException)
        {
            return NotFound();
        }
        catch (SubscriptionValidationException ex)
        {
            return Invalid(ex);
        }
        catch (EventStoreException ex) when (ex.Failure.Kind == EventStoreFailureKind.ConcurrencyConflict)
        {
            SecurityLog.Info(loggers.CreateLogger(SecurityEvents.Category), SecurityEvents.ConcurrencyConflict, memberId);
            var current = await service.GetAsync(bare, context.RequestAborted).ConfigureAwait(false);
            return current is null
                ? NotFound()
                : Problem.Result(412, ProblemCodes.ConcurrencyConflict, "The subscription was changed by someone else.",
                    new Dictionary<string, object?> { ["currentVersion"] = current.Version });
        }
    }

    private static IResult Detail(HttpContext context, SubscriptionState state, TimeProvider time, int status)
    {
        context.Response.Headers.ETag = EntityTags.Format(state.Version);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(SubscriptionDetailDto.From(state, SubscriptionCalculator.Today(time)), statusCode: status);
    }

    private static IResult Json<T>(HttpContext context, T value)
    {
        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(value);
    }

    private static IResult NotFound() => Problem.Result(404, ProblemCodes.NotFound, "Subscription not found.");

    private static IResult Invalid(SubscriptionValidationException ex) =>
        ValidationProblems.Result(ex.Errors.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal));
}
