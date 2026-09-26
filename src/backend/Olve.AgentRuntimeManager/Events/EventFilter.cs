using Olve.AgentRuntimeManager.Api;
using Olve.Results;

namespace Olve.AgentRuntimeManager.Events;

/// <summary>
/// The server-side <c>event</c> / <c>exclude_event</c> filter of <c>GET /api/events</c>. Each entry
/// is an event name (<c>session.created</c>) or a namespace (<c>session.*</c>); an event passes
/// if it matches an include entry (or there are none) and no exclude entry.
/// </summary>
/// <remarks>
/// Names are checked against the contract's events (<see cref="ArmEvent.EventTypes"/>): a name
/// or namespace no event has is a 400, so a typo fails loudly instead of streaming nothing.
/// Heartbeats keep the connection alive and always pass; naming <c>heartbeat</c> is a 400 too.
/// </remarks>
public sealed class EventFilter
{
    public const string HeartbeatType = "heartbeat";

    /// <summary>The names a filter may use: every event but the heartbeat.</summary>
    public static IReadOnlyList<string> FilterableTypes { get; } = [.. ArmEvent.EventTypes.Where(t => t != HeartbeatType)];

    /// <summary>Passes every event.</summary>
    public static EventFilter All { get; } = new([], []);

    private readonly IReadOnlyList<string> _include;
    private readonly IReadOnlyList<string> _exclude;

    private EventFilter(IReadOnlyList<string> include, IReadOnlyList<string> exclude)
    {
        _include = include;
        _exclude = exclude;
    }

    /// <summary>Validates the filter's entries; unknown names and namespaces are problems.</summary>
    public static Result<EventFilter> Parse(IReadOnlyList<string>? include, IReadOnlyList<string>? exclude)
    {
        include ??= [];
        exclude ??= [];
        var problems = new List<ResultProblem>();
        foreach (var (parameter, entries) in new[] { ("event", include), ("exclude_event", exclude) })
        {
            foreach (var entry in entries.Where(e => !FilterableTypes.Any(type => Matches(e, type))))
            {
                problems.Add(new ResultProblem(
                    "'{0}' has unknown event '{1}'. Known events: {2} (or a namespace such as 'session.*').",
                    parameter, entry, string.Join(", ", FilterableTypes)));
            }
        }

        return problems.Count > 0 ? new ResultProblemCollection(problems) : new EventFilter(include, exclude);
    }

    public bool Matches(string eventType) =>
        eventType == HeartbeatType ||
        ((_include.Count == 0 || _include.Any(e => Matches(e, eventType))) && !_exclude.Any(e => Matches(e, eventType)));

    private static bool Matches(string entry, string eventType) =>
        entry.EndsWith(".*", StringComparison.Ordinal)
            ? entry.Length > 2 && eventType.StartsWith(entry[..^1], StringComparison.Ordinal)
            : entry == eventType;
}
