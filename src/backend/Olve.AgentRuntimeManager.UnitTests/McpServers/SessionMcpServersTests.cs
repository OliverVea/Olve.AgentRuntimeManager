using Olve.AgentRuntimeManager.McpServers;

namespace Olve.AgentRuntimeManager.UnitTests.McpServers;

/// <summary>A session's MCP servers (M10c): what's taken from the registry when it's created.</summary>
public class SessionMcpServersTests
{
    [Test]
    public async Task Named_AreTakenOnce_ArmsToolsAreLeftOut_AndNothingElseIsKnown()
    {
        var store = new InMemoryMcpServerStore().With("github").With("unused");

        var (servers, unknown) = SessionMcpServers.Resolve(["arm_bash", "github", "nope", "github", "nope"], store);

        await Assert.That(servers.Select(s => s.Name)).IsEquivalentTo(["github"]);
        await Assert.That(servers[0].Command).IsEqualTo("npx");
        await Assert.That(servers[0].Args).IsEquivalentTo(["-y", "github"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(servers[0].Env["TOKEN"]).IsEqualTo("github");
        await Assert.That(unknown).IsEquivalentTo(["nope"]);
    }

    [Test]
    public async Task ASessionThatNamesNoTools_HasNone()
    {
        var (servers, unknown) = SessionMcpServers.Resolve(null, new InMemoryMcpServerStore().With("github"));

        await Assert.That(servers).IsEmpty();
        await Assert.That(unknown).IsEmpty();
    }
}
