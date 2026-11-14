using Microsoft.AspNetCore.Http;
using Subscribed.Platform.Problems;

namespace Subscribed.Platform.Http;

public static class IfMatch
{
    /// <summary>
    /// Reads a quoted version from <c>If-Match</c>. A missing header is <c>428 if-match-required</c>; a malformed one is
    /// <c>400</c> with <c>errors.ifMatch</c>.
    /// </summary>
    public static bool TryRead(HttpContext context, out long version, out IResult? problem)
    {
        ArgumentNullException.ThrowIfNull(context);
        version = 0;
        var header = context.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            problem = Problem.Result(428, ProblemCodes.IfMatchRequired, "An If-Match header with the current version is required.");
            return false;
        }

        if (!EntityTags.TryParse(header, out version))
        {
            problem = ValidationProblems.Result("ifMatch", "If-Match must be a quoted version, for example \"3\".");
            return false;
        }

        problem = null;
        return true;
    }
}
