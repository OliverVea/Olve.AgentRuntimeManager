using Olve.AgentRuntimeManager.Events;
using Olve.Results.TUnit;

namespace Olve.AgentRuntimeManager.UnitTests.Events;

public class EventFilterTests
{
    private static EventFilter Filter(string[]? include, string[]? exclude)
    {
        EventFilter.Parse(include, exclude).TryPickValue(out var filter);
        return filter!;
    }

    [Test]
    [Arguments(null, null, "session.created", true)]
    [Arguments(new[] { "session.created" }, null, "session.created", true)]
    [Arguments(new[] { "session.created" }, null, "session.killed", false)]
    [Arguments(new[] { "session.*" }, null, "session.killed", true)]
    [Arguments(null, new[] { "session.killed" }, "session.killed", false)]
    [Arguments(null, new[] { "session.killed" }, "session.created", true)]
    [Arguments(new[] { "session.*" }, new[] { "session.started" }, "session.started", false)]
    [Arguments(new[] { "session.*" }, new[] { "session.started" }, "session.created", true)]
    [Arguments(new[] { "session.created" }, new[] { "session.*" }, "session.created", false)]
    public async Task Matches_IncludesThenExcludes(string[]? include, string[]? exclude, string eventType, bool expected) =>
        await Assert.That(Filter(include, exclude).Matches(eventType)).IsEqualTo(expected);

    [Test]
    [Arguments(new[] { "session.created" }, null)]
    [Arguments(null, new[] { "session.*" })]
    public async Task Heartbeats_AlwaysPass(string[]? include, string[]? exclude) =>
        await Assert.That(Filter(include, exclude).Matches("heartbeat")).IsTrue();

    [Test]
    [Arguments("session.exploded")]
    [Arguments("sessions.*")]
    [Arguments("session")]
    [Arguments(".*")]
    [Arguments("*")]
    [Arguments("heartbeat")]
    public async Task UnknownNames_AreProblems(string entry)
    {
        await Assert.That(EventFilter.Parse([entry], null)).Failed();
        await Assert.That(EventFilter.Parse(null, [entry])).Failed();
    }

    [Test]
    public async Task EveryUnknownName_IsReported()
    {
        var result = EventFilter.Parse(["nope", "session.created"], ["nada"]);

        result.TryPickProblems(out var problems);
        await Assert.That(problems!.Count()).IsEqualTo(2);
    }

    [Test]
    public async Task FilterableTypes_AreTheContractsEventsButTheHeartbeat() =>
        await Assert.That(EventFilter.FilterableTypes).IsEquivalentTo([
            "session.created", "session.queued", "session.started", "session.waiting", "session.resumed",
            "session.completed", "session.failed", "session.killed",
        ]);
}
