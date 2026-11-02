using System.Runtime.Versioning;
using Olve.AgentRuntimeManager.Supervisor.Protocol;

namespace Olve.AgentRuntimeManager.UnitTests.Tools;

/// <summary>
/// An external MCP server as a bash script: every line it's sent goes to <see cref="Received"/>,
/// its answers are bash the test can replace, with <c>$id</c> the request's id.
/// </summary>
/// <remarks>
/// A fake, so an assumption about real servers. Its answers have the shape of
/// <c>@modelcontextprotocol/server-memory@2.0.1</c>'s, captured from it on 2026-11-02 over stdio,
/// protocol 2025-11-25 (the keys in the order it sends them: <c>result</c> before <c>jsonrpc</c>
/// and <c>id</c>). Refresh them with that server's next version.
/// </remarks>
[UnsupportedOSPlatform("windows")]
internal sealed class StubMcpServer
{
    public const string Answer = "printf '%s\\n' ";

    public const string InitializeAnswer =
        Answer + """'{"result":{"protocolVersion":"2025-11-25","capabilities":{"tools":{"listChanged":true},"resources":{"listChanged":true,"subscribe":true}},"serverInfo":{"name":"memory-server","version":"2.0.1"}},"jsonrpc":"2.0","id":'$id'}'""";

    public const string ToolsAnswer =
        Answer + """'{"result":{"tools":[{"name":"create_entities","title":"Create Entities","description":"Create them.","inputSchema":{"type":"object","properties":{"entities":{"type":"array"}},"required":["entities"]},"outputSchema":{"type":"object"}},{"name":"dotted.name","description":"Named oddly.","inputSchema":{"type":"object"}}]},"jsonrpc":"2.0","id":'$id'}'""";

    public const string CallAnswer =
        Answer + """'{"result":{"content":[{"type":"text","text":"created"},{"type":"image","data":"aGk=","mimeType":"image/png"}],"structuredContent":{"made":1}},"jsonrpc":"2.0","id":'$id'}'""";

    private readonly ToolsTestFolder _folder;
    private readonly string _name;

    public StubMcpServer(ToolsTestFolder folder, string name = "stub")
    {
        _folder = folder;
        _name = name;
    }

    /// <summary>What it runs first, before it reads anything.</summary>
    public string Before { get; init; } = "";

    public string Initialize { get; init; } = InitializeAnswer;
    public string Tools { get; init; } = ToolsAnswer;
    public string Call { get; init; } = CallAnswer;

    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();

    /// <summary>The file of what it was sent, one line each.</summary>
    public string Received => _folder.PathOf($"{_name}-received.jsonl");

    public string[] ReceivedLines() => File.Exists(Received) ? File.ReadAllLines(Received) : [];

    public McpServerDefinition Definition()
    {
        var script = _folder.Write($"{_name}.sh", $$"""
            {{Before}}
            while IFS= read -r line; do
              printf '%s\n' "$line" >> '{{Received}}'
              [[ $line =~ \"id\":([0-9]+) ]] && id=${BASH_REMATCH[1]}
              case "$line" in
                *'"method":"initialize"'*) {{Initialize}} ;;
                *'"method":"tools/list"'*) {{Tools}} ;;
                *'"method":"tools/call"'*) {{Call}} ;;
              esac
            done
            """);
        return new McpServerDefinition { Name = _name, Command = "bash", Args = [script], Env = Env };
    }
}
