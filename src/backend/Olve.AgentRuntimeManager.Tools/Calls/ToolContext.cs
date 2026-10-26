using System.Runtime.Versioning;
using Olve.AgentRuntimeManager.Tools.Tools;

namespace Olve.AgentRuntimeManager.Tools.Calls;

/// <summary>What every call runs with: the workplace relative paths resolve from, and the running calls' process groups.</summary>
[UnsupportedOSPlatform("windows")]
internal sealed record ToolContext(string Workplace, ProcessGroups Groups);
