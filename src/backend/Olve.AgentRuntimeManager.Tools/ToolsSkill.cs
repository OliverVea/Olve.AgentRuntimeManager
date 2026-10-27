namespace Olve.AgentRuntimeManager.Tools;

/// <summary>One of the session's skills as the server got it (<c>--skill name=folder</c>): the name the agent loads it by, and its folder (absolute).</summary>
internal sealed record ToolsSkill(string Name, string Folder);
