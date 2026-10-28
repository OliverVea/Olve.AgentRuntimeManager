using System.Runtime.Versioning;
using Olve.AgentRuntimeManager.Tools.Tools;

namespace Olve.AgentRuntimeManager.Tools.Calls;

/// <summary>
/// What every call runs with: the workplace relative paths resolve from, the running calls' process
/// groups, and the session's skills (M10b, what <c>arm_skill_load</c> loads).
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed record ToolContext(string Workplace, ProcessGroups Groups, SessionSkills Skills);
