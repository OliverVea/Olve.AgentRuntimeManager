namespace Olve.AgentRuntimeManager.McpServers;

/// <summary>Where registered MCP servers are kept.</summary>
public interface IMcpServerStore
{
    /// <summary>Every registered server, by name.</summary>
    IReadOnlyList<McpServerRecord> List();

    McpServerRecord? Find(string name);

    /// <summary>Registers a server, or replaces the one with its name.</summary>
    void Set(McpServerRecord server);

    /// <summary>False if there was none by that name.</summary>
    bool Delete(string name);
}
