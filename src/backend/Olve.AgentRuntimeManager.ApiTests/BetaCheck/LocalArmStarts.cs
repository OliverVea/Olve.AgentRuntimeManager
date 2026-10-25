using TUnit.Core.Interfaces;

namespace Olve.AgentRuntimeManager.ApiTests.BetaCheck;

/// <summary>
/// How many beta check tests run at once, each starting an ARM of its own (<see cref="LocalArm"/>):
/// on the gate's host, shared with other pipelines, a dozen starting together didn't become healthy.
/// </summary>
public sealed class LocalArmStarts : IParallelLimit
{
    public int Limit => 2;
}
