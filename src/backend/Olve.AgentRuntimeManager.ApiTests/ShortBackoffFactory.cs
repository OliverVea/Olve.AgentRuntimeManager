namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>An in-process host whose paused providers wait only moments, to drive retries end to end.</summary>
public sealed class ShortBackoffFactory : ApiFactory
{
    protected override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string>
    {
        ["Sessions:TotalSlots"] = "10",
        ["Sessions:ProviderRetries"] = "2",
        ["Sessions:ProviderBackoff"] = "00:00:00.200",
        ["Sessions:ProviderMaxBackoff"] = "00:00:00.400",
    };
}
