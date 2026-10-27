namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>An in-process host with default tools configured (<c>Tools__Default__0</c>, …), as a deployment would.</summary>
public sealed class DefaultToolsFactory : ApiFactory
{
    protected override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string>
    {
        ["Sessions:TotalSlots"] = "10",
        ["Tools:Default:0"] = "arm_file_read",
        ["Tools:Default:1"] = "arm_bash",
    };
}
