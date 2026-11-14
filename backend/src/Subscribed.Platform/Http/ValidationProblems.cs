using Microsoft.AspNetCore.Http;
using Subscribed.Platform.Problems;

namespace Subscribed.Platform.Http;

public static class ValidationProblems
{
    /// <summary>400 validation-failed with camelCase <c>errors</c>. Keys are used exactly as given.</summary>
    public static IResult Result(IDictionary<string, string[]> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        return Problem.Result(400, ProblemCodes.ValidationFailed, "Validation failed",
            new Dictionary<string, object?> { ["errors"] = errors });
    }

    public static IResult Result(string field, string message) =>
        Result(new Dictionary<string, string[]> { [field] = [message] });
}
