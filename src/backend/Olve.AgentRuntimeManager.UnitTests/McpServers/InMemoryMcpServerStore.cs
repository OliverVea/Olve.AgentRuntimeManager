using Olve.AgentRuntimeManager.McpServers;

namespace Olve.AgentRuntimeManager.UnitTests.McpServers;

/// <summary>An <see cref="IMcpServerStore"/> in a dictionary.</summary>
public sealed class InMemoryMcpServerStore : IMcpServerStore
{
    private readonly Dictionary<string, McpServerRecord> _servers = new(StringComparer.Ordinal);

    public IReadOnlyList<McpServerRecord> List() => [.. _servers.Values.OrderBy(s => s.Name, StringComparer.Ordinal)];

    public McpServerRecord? Find(string name) => _servers.GetValueOrDefault(name);

    public void Set(McpServerRecord server) => _servers[server.Name] = server;

    public bool Delete(string name) => _servers.Remove(name);

    public InMemoryMcpServerStore With(string name, string command = "npx")
    {
        Set(new McpServerRecord
        {
            Name = name, Description = "A server.", Command = command, Args = ["-y", name], Env = new Dictionary<string, string> { ["TOKEN"] = name },
            UpdatedAt = DateTimeOffset.UnixEpoch,
        });
        return this;
    }
}
