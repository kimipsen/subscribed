using System.Text.RegularExpressions;
using Marten.Exceptions;
using Npgsql;

namespace Subscribed.Platform.EventStore;

public enum EventStoreFailureKind
{
    /// <summary>The stream changed since it was read, or a concurrent creation won. HTTP 412.</summary>
    ConcurrencyConflict,

    /// <summary>The app role lacks a privilege (SQLSTATE 42501). HTTP 500, readiness fails.</summary>
    PrivilegeMissing,

    /// <summary>The database cannot be reached, or the schema is not migrated. HTTP 503.</summary>
    DatabaseUnavailable,
}

public sealed record EventStoreFailure(EventStoreFailureKind Kind, string? ObjectName = null);

/// <summary>Thrown by module services after <see cref="EventStoreErrors.Map"/> classified a failure.</summary>
public sealed class EventStoreException(EventStoreFailure failure, Exception inner)
    : Exception($"Event store failure: {failure.Kind}", inner)
{
    public EventStoreFailure Failure { get; } = failure;
}

/// <summary>
/// The only place that knows library exception types (design §5). The types below are the
/// B-1/E6 results recorded in backend/README.md for Marten 9.59.0 + Npgsql 9.0.4:
/// (a) concurrent append at the same expected version and (b) stale expected version both throw
/// <c>JasperFx.Events.EventStreamUnexpectedMaxEventIdException</c> (base <c>JasperFx.ConcurrencyException</c>);
/// (c) concurrent creation of the same stream throws <c>Marten.Exceptions.ExistingStreamIdCollisionException</c>.
/// None of them carries an inner <see cref="PostgresException"/>.
/// </summary>
public static partial class EventStoreErrors
{
    private static readonly string[] ConflictTables = ["mt_events", "mt_streams"];

    [GeneratedRegex(@"permission denied for (?:table|sequence|schema|function|relation) (?<name>[A-Za-z0-9_.]+)", RegexOptions.CultureInvariant)]
    private static partial Regex PermissionDenied();

    /// <summary>Returns the classified failure, or null when the exception must be rethrown (500).</summary>
    public static EventStoreFailure? Map(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        foreach (var e in Flatten(exception))
        {
            switch (e)
            {
                case JasperFx.ConcurrencyException:
                case ExistingStreamIdCollisionException:
                    return new EventStoreFailure(EventStoreFailureKind.ConcurrencyConflict);
                case PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
                    when pg.TableName is not null && ConflictTables.Contains(pg.TableName, StringComparer.Ordinal):
                    return new EventStoreFailure(EventStoreFailureKind.ConcurrencyConflict, pg.TableName);
                case PostgresException { SqlState: PostgresErrorCodes.InsufficientPrivilege } pg:
                    var match = PermissionDenied().Match(pg.MessageText);
                    return new EventStoreFailure(EventStoreFailureKind.PrivilegeMissing, match.Success ? match.Groups["name"].Value : pg.TableName);
                case PostgresException pg when IsUnavailable(pg.SqlState):
                    return new EventStoreFailure(EventStoreFailureKind.DatabaseUnavailable);
                case NpgsqlException and not PostgresException:
                    return new EventStoreFailure(EventStoreFailureKind.DatabaseUnavailable);
            }
        }

        return null;
    }

    /// <summary>Wraps a classified failure; rethrows anything else unchanged.</summary>
    public static async Task<T> GuardAsync<T>(Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not EventStoreException && Map(ex) is { } failure)
        {
            throw new EventStoreException(failure, ex);
        }
    }

    private static bool IsUnavailable(string sqlState) =>
        sqlState.StartsWith("08", StringComparison.Ordinal) // connection exception
        || sqlState.StartsWith("57P", StringComparison.Ordinal) // operator intervention (shutdown)
        || sqlState.StartsWith("53", StringComparison.Ordinal) // insufficient resources
        || sqlState == PostgresErrorCodes.UndefinedTable // schema not migrated
        || sqlState == PostgresErrorCodes.InvalidSchemaName
        || sqlState == PostgresErrorCodes.InvalidCatalogName
        || sqlState == PostgresErrorCodes.InvalidPassword
        || sqlState == PostgresErrorCodes.InvalidAuthorizationSpecification;

    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        var stack = new Stack<Exception>();
        stack.Push(exception);
        var depth = 0;
        while (stack.Count > 0 && depth++ < 32)
        {
            var e = stack.Pop();
            yield return e;
            if (e is AggregateException agg)
            {
                foreach (var inner in agg.InnerExceptions)
                {
                    stack.Push(inner);
                }
            }
            else if (e.InnerException is not null)
            {
                stack.Push(e.InnerException);
            }
        }
    }
}
