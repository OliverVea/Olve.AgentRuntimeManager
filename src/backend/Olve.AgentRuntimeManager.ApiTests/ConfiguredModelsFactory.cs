namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>
/// An in-process host whose providers' models are configured, as an environment would (env vars
/// <c>Providers__Fake__Models__0__Id</c>, …): Claude's display names left blank, the fake provider
/// with a list of its own.
/// </summary>
public sealed class ConfiguredModelsFactory : ApiFactory
{
    protected override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string>
    {
        ["Sessions:TotalSlots"] = "10",
        ["Providers:Claude:DisplayName"] = "",
        ["Providers:Claude:Models:0:Id"] = "claude-opus-4-5",
        ["Providers:Fake:Models:0:Id"] = "small",
        ["Providers:Fake:Models:0:DisplayName"] = "Small",
        ["Providers:Fake:Models:1:Id"] = "large",
    };
}
