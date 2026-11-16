using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Subscribed.Household;
using Subscribed.Platform.EventStore;
using Subscribed.Platform.Http;
using Subscribed.Platform.Identity;
using Subscribed.Platform.Problems;

namespace Subscribed.Subscriptions.PaymentOptions;

public static class PaymentOptionEndpoints
{
    public static IEndpointRouteBuilder MapPaymentOptionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup("/api/payment-options").RequireAuthorization(HouseholdMembership.PolicyName);
        group.MapGet("", ListAsync);
        group.MapGet("suggestions", SuggestionsAsync);
        group.MapGet("{id}", GetAsync);
        group.MapPost("", CreateAsync);
        group.MapPut("{id}", UpdateAsync);
        group.MapDelete("{id}", RemoveAsync);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(HttpContext context, PaymentOptionService service)
    {
        var options = await service.ListAsync(context.RequestAborted).ConfigureAwait(false);
        return Json(context, new PaymentOptionListDto([.. options.Select(PaymentOptionDto.From)]));
    }

    private static IResult SuggestionsAsync(HttpContext context) => Json(context, PaymentOptionSuggestionsDto.Default);

    private static async Task<IResult> GetAsync(string id, HttpContext context, PaymentOptionService service)
    {
        if (!PaymentOptionIds.TryParse(id, out var bare))
        {
            return NotFound();
        }

        var state = await service.GetAsync(bare, context.RequestAborted).ConfigureAwait(false);
        return state is null ? NotFound() : Option(context, state, StatusCodes.Status200OK);
    }

    private static async Task<IResult> CreateAsync(HttpContext context, PaymentOptionService service, ILoggerFactory loggers)
    {
        var body = await JsonRequest.ReadAsync<PaymentOptionRequest>(context, SubscriptionEndpoints.RequestJson).ConfigureAwait(false);
        if (body.Error is { } error)
        {
            return error;
        }

        PaymentOptionFields command;
        try
        {
            command = PaymentOptionDecisions.ParseCreate(body.Value!.ToInput());
        }
        catch (PaymentOptionRejectedException ex)
        {
            return Rejected(ex, loggers);
        }

        var memberId = HouseholdMembership.MemberIdFor(context);
        var created = await service.CreateAsync(command, WriteContext.Member(memberId, Problem.TraceId(context)), context.RequestAborted).ConfigureAwait(false);
        var bare = PaymentOptionIds.Bare(created.Id);
        PaymentOptionLog.Created(loggers.CreateLogger(PaymentOptionLog.Category), bare);
        context.Response.Headers.Location = "/api/payment-options/" + bare;
        return Option(context, created, StatusCodes.Status201Created);
    }

    /// <summary>
    /// Processed in the same order as the subscription writes: 1 id format (404), 2 If-Match (428 / 400), 3 body (413, 415, 400),
    /// 4 field shape (400), then the service: 5 load (404, a removed option included), 6 stale version (412), 7 append (412 on a race).
    /// </summary>
    private static async Task<IResult> UpdateAsync(string id, HttpContext context, PaymentOptionService service, ILoggerFactory loggers)
    {
        if (!PaymentOptionIds.TryParse(id, out var bare))
        {
            return NotFound();
        }

        if (!IfMatch.TryRead(context, out var version, out var problem))
        {
            return problem!;
        }

        var body = await JsonRequest.ReadAsync<PaymentOptionRequest>(context, SubscriptionEndpoints.RequestJson).ConfigureAwait(false);
        if (body.Error is { } error)
        {
            return error;
        }

        PaymentOptionFields command;
        try
        {
            command = PaymentOptionDecisions.ParseUpdate(body.Value!.ToInput());
        }
        catch (PaymentOptionRejectedException ex)
        {
            return Rejected(ex, loggers);
        }

        return await GuardedAsync(bare, context, service, loggers,
            memberId => service.UpdateAsync(bare, version, command, WriteContext.Member(memberId, Problem.TraceId(context)), context.RequestAborted),
            state => Option(context, state, StatusCodes.Status200OK)).ConfigureAwait(false);
    }

    private static async Task<IResult> RemoveAsync(string id, HttpContext context, PaymentOptionService service, ILoggerFactory loggers)
    {
        if (!PaymentOptionIds.TryParse(id, out var bare))
        {
            return NotFound();
        }

        if (!IfMatch.TryRead(context, out var version, out var problem))
        {
            return problem!;
        }

        return await GuardedAsync(bare, context, service, loggers,
            memberId => service.RemoveAsync(bare, version, WriteContext.Member(memberId, Problem.TraceId(context)), context.RequestAborted),
            _ =>
            {
                PaymentOptionLog.Removed(loggers.CreateLogger(PaymentOptionLog.Category), bare);
                context.Response.Headers.CacheControl = "no-store";
                return Results.NoContent();
            }).ConfigureAwait(false);
    }

    private static async Task<IResult> GuardedAsync(
        string bare,
        HttpContext context,
        PaymentOptionService service,
        ILoggerFactory loggers,
        Func<string, Task<PaymentOptionState>> action,
        Func<PaymentOptionState, IResult> respond)
    {
        var memberId = HouseholdMembership.MemberIdFor(context);
        try
        {
            return respond(await action(memberId).ConfigureAwait(false));
        }
        catch (PaymentOptionNotFoundException)
        {
            return NotFound();
        }
        catch (EventStoreException ex) when (ex.Failure.Kind == EventStoreFailureKind.ConcurrencyConflict)
        {
            SecurityLog.Info(loggers.CreateLogger(SecurityEvents.Category), SecurityEvents.ConcurrencyConflict, memberId);
            var current = await service.GetAsync(bare, context.RequestAborted).ConfigureAwait(false);
            return current is null
                ? NotFound()
                : Problem.Result(412, ProblemCodes.ConcurrencyConflict, "The payment option was changed by someone else.",
                    new Dictionary<string, object?> { ["currentVersion"] = current.Version });
        }
    }

    private static IResult Option(HttpContext context, PaymentOptionState state, int status)
    {
        context.Response.Headers.ETag = EntityTags.Format(state.Version);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(PaymentOptionDto.From(state), statusCode: status);
    }

    private static IResult Json<T>(HttpContext context, T value)
    {
        context.Response.Headers.CacheControl = "no-store";
        return Results.Json(value);
    }

    private static IResult NotFound() => Problem.Result(404, ProblemCodes.NotFound, "Payment option not found.");

    /// <summary>The 400 carries fixed messages only, and the log gets reason codes only (NFR-2).</summary>
    private static IResult Rejected(PaymentOptionRejectedException ex, ILoggerFactory loggers)
    {
        PaymentOptionLog.Rejected(loggers.CreateLogger(PaymentOptionLog.Category), ex);
        return ValidationProblems.Result(ex.Errors.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal));
    }
}
