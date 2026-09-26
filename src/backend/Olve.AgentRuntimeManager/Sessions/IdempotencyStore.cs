using Microsoft.Extensions.Options;

namespace Olve.AgentRuntimeManager.Sessions;

/// <summary>
/// Replays the response of an earlier request with the same <c>Idempotency-Key</c> within
/// <see cref="SessionOptions.IdempotencyWindow"/> (SPEC: 24 hours) instead of acting again. Only
/// successes are kept, so a failed request (a full queue) can be retried with its key. In memory.
/// </summary>
/// <remarks>Keys are global for now; M7 scopes them to the caller's identity.</remarks>
public sealed class IdempotencyStore<TResponse>(TimeProvider time, IOptions<SessionOptions> options)
    where TResponse : class
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (DateTimeOffset At, TResponse Response)> _responses = [];

    /// <summary>
    /// The response stored under <paramref name="key"/>, else the result of <paramref name="act"/>
    /// (stored if <paramref name="keep"/> says so). Requests with the same key run one at a time.
    /// </summary>
    public TResponse GetOrAct(string? key, Func<TResponse> act, Func<TResponse, bool> keep)
    {
        if (key is null)
        {
            return act();
        }

        lock (_gate)
        {
            var now = time.GetUtcNow();
            foreach (var expired in _responses.Where(r => now - r.Value.At >= options.Value.IdempotencyWindow).Select(r => r.Key).ToList())
            {
                _responses.Remove(expired);
            }

            if (_responses.TryGetValue(key, out var stored))
            {
                return stored.Response;
            }

            var response = act();
            if (keep(response))
            {
                _responses[key] = (now, response);
            }

            return response;
        }
    }
}
