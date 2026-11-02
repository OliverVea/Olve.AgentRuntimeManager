using Olve.AgentRuntimeManager.Supervisor.Protocol;
using Olve.AgentRuntimeManager.Tools.Protocol;

namespace Olve.AgentRuntimeManager.McpServers;

/// <summary>
/// A new session's MCP servers (M10c): the registered ones it names in <c>tools</c>, as they are
/// now. Taken once, when it's created, so a later change or delete doesn't touch it.
/// </summary>
public static class SessionMcpServers
{
    /// <summary>The definitions of the servers named, and the names that are neither one of ARM's tools nor registered.</summary>
    public static (IReadOnlyList<McpServerDefinition> Servers, IReadOnlyList<string> Unknown) Resolve(IReadOnlyList<string>? tools, IMcpServerStore store)
    {
        List<McpServerDefinition> servers = [];
        List<string> unknown = [];
        foreach (var name in (tools ?? []).Distinct(StringComparer.Ordinal).Where(name => ToolCatalog.Find(name) is null))
        {
            if (store.Find(name) is { } server)
            {
                servers.Add(new McpServerDefinition { Name = server.Name, Command = server.Command, Args = server.Args, Env = server.Env });
            }
            else
            {
                unknown.Add(name);
            }
        }

        return (servers, unknown);
    }
}
