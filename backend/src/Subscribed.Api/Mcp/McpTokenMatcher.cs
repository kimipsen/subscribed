using Subscribed.Household;

namespace Subscribed.Api.Mcp;

/// <summary>
/// Finds the stored token a presented secret matches. It compares against EVERY stored entry with the constant-time comparison and
/// never stops at the first match, so timing does not reveal which row matched. The compare function is a parameter so a test can count calls.
/// </summary>
internal static class McpTokenMatcher
{
    public static HouseholdViewMcpToken? Find(IEnumerable<HouseholdViewMcpToken> tokens, string presented, Func<string?, string?, bool>? matches = null)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        matches ??= McpTokenSecrets.Matches;
        HouseholdViewMcpToken? found = null;
        foreach (var token in tokens)
        {
            if (matches(presented, token.SecretHash) && found is null)
            {
                found = token;
            }
        }

        return found;
    }
}
