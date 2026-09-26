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
    [Arguments(null, null, "message.created", true)]
    [Arguments(new[] { "message.created" }, null, "message.created", true)]
    [Arguments(new[] { "message.created" }, null, "message.deleted", false)]
    [Arguments(new[] { "message.*" }, null, "message.deleted", true)]
    [Arguments(null, new[] { "message.deleted" }, "message.deleted", false)]
    [Arguments(null, new[] { "message.deleted" }, "message.created", true)]
    [Arguments(new[] { "message.*" }, new[] { "message.updated" }, "message.updated", false)]
    [Arguments(new[] { "message.*" }, new[] { "message.updated" }, "message.created", true)]
    [Arguments(new[] { "message.created" }, new[] { "message.*" }, "message.created", false)]
    public async Task Matches_IncludesThenExcludes(string[]? include, string[]? exclude, string eventType, bool expected) =>
        await Assert.That(Filter(include, exclude).Matches(eventType)).IsEqualTo(expected);

    [Test]
    [Arguments(new[] { "message.created" }, null)]
    [Arguments(null, new[] { "message.*" })]
    public async Task Heartbeats_AlwaysPass(string[]? include, string[]? exclude) =>
        await Assert.That(Filter(include, exclude).Matches("heartbeat")).IsTrue();

    [Test]
    [Arguments("message.exploded")]
    [Arguments("messages.*")]
    [Arguments("message")]
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
        var result = EventFilter.Parse(["nope", "message.created"], ["nada"]);

        result.TryPickProblems(out var problems);
        await Assert.That(problems!.Count()).IsEqualTo(2);
    }

    [Test]
    public async Task FilterableTypes_AreTheContractsEventsButTheHeartbeat() =>
        await Assert.That(EventFilter.FilterableTypes).IsEquivalentTo(["message.created", "message.updated", "message.deleted"]);
}
