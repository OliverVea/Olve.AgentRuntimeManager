using System.Text.Json.Nodes;

namespace Olve.AgentRuntimeManager.Sessions.Providers.Claude;

/// <summary>
/// What a turn's events said before its result, for telling the provider's trouble from the
/// session's: whether the model wrote anything, the kind of API error Claude Code reported, and
/// whether the usage limit was hit (and when it resets).
/// </summary>
internal readonly record struct ClaudeSignals(bool ModelAnswered, bool LimitReached, DateTimeOffset? LimitResetsAt, string? ApiError = null)
{
    public ClaudeSignals Read(JsonObject e)
    {
        if (ClaudeStreamJson.IsModelMessage(e))
        {
            return this with { ModelAnswered = true };
        }

        if (ClaudeStreamJson.ApiError(e) is { } apiError)
        {
            return this with { ApiError = apiError };
        }

        return ClaudeStreamJson.IsLimitReached(e, out var resetsAt)
            ? this with { LimitReached = true, LimitResetsAt = resetsAt }
            : this;
    }
}
