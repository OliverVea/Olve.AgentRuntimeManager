namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>An in-process host with one slot and room for one queued session, to drive 202 and 503.</summary>
public sealed class SmallQueueFactory : ApiFactory
{
    protected override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string>
    {
        ["Sessions:TotalSlots"] = "1",
        ["Sessions:MaxQueueSize"] = "1",
    };
}
