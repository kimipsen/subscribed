using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Subscribed.Platform.Problems;

namespace Subscribed.Platform.Http;

public readonly record struct JsonRequestResult<T>(T? Value, IResult? Error)
    where T : class;

/// <summary>Reads a bounded JSON object body. The caller supplies the serializer options.</summary>
public static class JsonRequest
{
    public const int MaxBodyBytes = 16 * 1024;
    public const string BodyKey = "body";

    public static async Task<JsonRequestResult<T>> ReadAsync<T>(HttpContext context, JsonSerializerOptions options)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);

        if (context.Request.ContentLength > MaxBodyBytes)
        {
            return Fail<T>(TooLarge());
        }

        if (!context.Request.HasJsonContentType())
        {
            return Fail<T>(Problem.Result(415, ProblemCodes.UnsupportedMediaType, "The request body must be application/json."));
        }

        var buffer = new byte[MaxBodyBytes + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await context.Request.Body.ReadAsync(buffer.AsMemory(total), context.RequestAborted).ConfigureAwait(false)) > 0)
        {
            total += read;
        }

        if (total > MaxBodyBytes)
        {
            return Fail<T>(TooLarge());
        }

        var bytes = buffer.AsSpan(0, total);
        if (!StartsWithObject(bytes))
        {
            return Fail<T>(ValidationProblems.Result(BodyKey, "The request body must be a JSON object."));
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(bytes, options);
            return value is null
                ? Fail<T>(ValidationProblems.Result(BodyKey, "The request body must be a JSON object."))
                : new JsonRequestResult<T>(value, null);
        }
        catch (JsonException ex)
        {
            var key = FirstPropertyOf(ex.Path) ?? BodyKey;
            return Fail<T>(ValidationProblems.Result(key, "The value is not valid."));
        }
    }

    /// <summary>
    /// <c>$.amount</c> → <c>amount</c>, <c>$.items[0].x</c> → <c>items</c>, <c>$</c> → null. Keys are camelCased like the
    /// response's other <c>errors</c> keys.
    /// </summary>
    internal static string? FirstPropertyOf(string? path)
    {
        if (string.IsNullOrEmpty(path) || !path.StartsWith("$.", StringComparison.Ordinal))
        {
            return null;
        }

        var rest = path.AsSpan(2);
        var end = rest.IndexOfAny('.', '[');
        var name = (end < 0 ? rest : rest[..end]).ToString();
        return name.Length == 0 ? null : JsonNamingPolicy.CamelCase.ConvertName(name);
    }

    private static bool StartsWithObject(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes);
        try
        {
            return reader.Read() && reader.TokenType == JsonTokenType.StartObject;
        }
        catch (JsonException)
        {
            // Malformed from the first token on: no property to blame.
            return false;
        }
    }

    private static JsonRequestResult<T> Fail<T>(IResult error)
        where T : class => new(null, error);

    private static IResult TooLarge() => Problem.Result(413, ProblemCodes.PayloadTooLarge, "The request body is too large.");
}
