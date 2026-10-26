namespace Olve.AgentRuntimeManager.Tools.Protocol;

/// <summary>
/// How ARM starts <c>olve-arm-tools</c> through the agent (the provider's MCP config):
/// <c>olve-arm-tools --workplace &lt;folder&gt; --journal &lt;file&gt; --tool &lt;name&gt; [--tool …]</c>.
/// It lists only the tools named and refuses calls to any other.
/// </summary>
public static class ToolsLaunch
{
    /// <summary>The MCP server's name: providers prefix its tools with it (<c>mcp__arm__arm_bash</c>), ARM strips that.</summary>
    public const string ServerName = "arm";

    /// <summary>The folder relative paths resolve from, and <c>arm_bash</c>'s default working directory.</summary>
    public const string Workplace = "--workplace";

    /// <summary>
    /// The session's record of calls by tool-use id, so no id runs twice: per session, not per run
    /// (it outlives a resume), in the session's folder next to its workplace.
    /// </summary>
    public const string Journal = "--journal";

    /// <summary>A tool to serve, by name (<see cref="ToolCatalog"/>); repeated for each.</summary>
    public const string Tool = "--tool";

    /// <summary>The journal's file name in the session's folder.</summary>
    public const string JournalFileName = "tools.jsonl";
}
