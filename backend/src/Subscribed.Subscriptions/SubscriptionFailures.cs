using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Subscribed.Household;
using Subscribed.Platform.EventStore;
using Subscribed.Platform.Http;
using Subscribed.Platform.Problems;
using Subscribed.Subscriptions.Domain;

namespace Subscribed.Subscriptions;

/// <summary>
/// One mapped subscription failure: the status and problem code the web API answers with, the field errors of a validation
/// failure and the current version a caller needs after a conflict. The MCP tools use the same values (BR-013, BR-014).
/// </summary>
public sealed record SubscriptionFailure(int Status, string Code, string Title, IDictionary<string, string[]>? Errors, long? CurrentVersion);

/// <summary>The single place that turns a subscription write exception into a failure. Endpoints and MCP tools both call it.</summary>
public static class SubscriptionFailures
{
    public static SubscriptionFailure NotFound { get; } = new(404, ProblemCodes.NotFound, "Subscription not found.", null, null);

    public static SubscriptionFailure Validation(SubscriptionValidationException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new(400, ProblemCodes.ValidationFailed, "Validation failed", exception.Errors.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal), null);
    }

    /// <summary>
    /// Maps <paramref name="exception"/>, or returns <see langword="null"/> when it is not a domain failure (the caller then lets it
    /// propagate). A state or concurrency failure reads the current version of the subscription; if it is gone the result is a 404.
    /// </summary>
    public static async Task<SubscriptionFailure?> MapAsync(Exception exception, string bareId, SubscriptionService service, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(service);
        switch (exception)
        {
            case SubscriptionNotFoundException:
                return NotFound;
            case SubscriptionValidationException validation:
                return Validation(validation);
            case SubscriptionStateException:
                {
                    var current = await service.GetAsync(bareId, ct).ConfigureAwait(false);
                    return current is null
                        ? NotFound
                        : new(409, ProblemCodes.InvalidSubscriptionState, "The subscription is not in a state that allows this change.", null, current.Version);
                }

            case EventStoreException { Failure.Kind: EventStoreFailureKind.ConcurrencyConflict }:
                {
                    var current = await service.GetAsync(bareId, ct).ConfigureAwait(false);
                    return current is null
                        ? NotFound
                        : new(412, ProblemCodes.ConcurrencyConflict, "The subscription was changed by someone else.", null, current.Version);
                }

            default:
                return null;
        }
    }

    public static IResult ToResult(SubscriptionFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure.Errors is not null)
        {
            return ValidationProblems.Result(failure.Errors);
        }

        return Problem.Result(failure.Status, failure.Code, failure.Title,
            failure.CurrentVersion is { } version ? new Dictionary<string, object?> { ["currentVersion"] = version } : null);
    }
}

/// <summary>Resolves the household zone from the household view. A zone this server cannot resolve falls back to UTC with a warning that carries only the household id.</summary>
public static class HouseholdZones
{
    public static TimeZoneInfo Resolve(HouseholdView view, ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(loggers);
        if (Subscribed.Household.Domain.TimeZoneIds.TryResolve(view.TimeZoneId, out var zone))
        {
            return zone;
        }

        HouseholdLog.TimeZoneUnresolvable(loggers.CreateLogger(HouseholdLog.Category), HouseholdService.StreamId);
        return TimeZoneInfo.Utc;
    }
}
