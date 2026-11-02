using System.Runtime.Versioning;
using System.Text.Json.Nodes;

namespace Olve.AgentRuntimeManager.Tools.External;

/// <summary>
/// A tool of an external MCP server as the agent sees it: <paramref name="Name"/> and the
/// <paramref name="Definition"/> for <c>tools/list</c> (the server's own, under that name), and the
/// <paramref name="RealName"/> the server is called by.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal sealed record ExternalTool(string Name, string RealName, ExternalServer Server, JsonObject Definition);
