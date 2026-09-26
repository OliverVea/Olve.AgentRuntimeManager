namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>An in-process host configured the way a deploy configures it (build version, environment).</summary>
public sealed class DeployedFactory : ApiFactory
{
    public const string Version = "2026.9.26.8";
    public const string Environment = "beta";

    protected override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string>
    {
        ["Arm:Version"] = Version,
        ["Arm:Environment"] = Environment,
    };
}
