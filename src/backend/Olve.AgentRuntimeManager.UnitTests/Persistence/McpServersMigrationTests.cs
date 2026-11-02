using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.McpServers;
using Olve.AgentRuntimeManager.Sessions;
using Olve.AgentRuntimeManager.Supervisor.Protocol;

namespace Olve.AgentRuntimeManager.UnitTests.Persistence;

/// <summary>
/// The MCP servers migration (M10c): sessions stored before it have none, a session keeps the
/// definitions it took, and the registry's table is there.
/// </summary>
public sealed class McpServersMigrationTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    [Test]
    public async Task ASessionFromBeforeMcpServers_HasNone()
    {
        var id = Guid.NewGuid();
        _database.Store().Add(Session(id));
        await using (var db = _database.Context())
        {
            // Back to the database as it was then (no McpServers column; the row stays), then up again.
            await db.GetService<IMigrator>().MigrateAsync("20261027154347_Skills");
            await db.Database.MigrateAsync();
        }

        await Assert.That(_database.Store().Get(id)!.McpServers).IsNull();
    }

    [Test]
    public async Task ASession_KeepsTheDefinitionsItTook()
    {
        var id = Guid.NewGuid();
        var definition = new McpServerDefinition
        {
            Name = "github", Command = "npx", Args = ["-y", "pkg@1.2.3"], Env = new Dictionary<string, string> { ["GITHUB_TOKEN"] = "t" },
        };

        _database.Store().Add(Session(id) with { McpServers = [definition] });

        var stored = _database.Store().Get(id)!.McpServers!.Single();
        await Assert.That((stored.Name, stored.Command)).IsEqualTo(("github", "npx"));
        await Assert.That(stored.Args).IsEquivalentTo(["-y", "pkg@1.2.3"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(stored.Env["GITHUB_TOKEN"]).IsEqualTo("t");
    }

    [Test]
    public async Task TheRegistry_KeepsAServer_WithItsArgsAndEnv()
    {
        var store = _database.McpServers();
        var server = new McpServerRecord
        {
            Name = "github", Description = "GitHub.", Command = "npx", Args = ["-y", "pkg@1.2.3"], Env = new Dictionary<string, string> { ["GITHUB_TOKEN"] = "t" },
            UpdatedAt = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero),
        };

        store.Set(server);
        store.Set(server with { Command = "uvx", Args = [], Env = new Dictionary<string, string>() });
        store.Set(server with { Name = "a" });

        var replaced = store.Find("github")!;
        await Assert.That(replaced.Command).IsEqualTo("uvx");
        await Assert.That(replaced.Args).IsEmpty();
        await Assert.That(replaced.Env).IsEmpty();
        await Assert.That(store.Find("a")!.Env["GITHUB_TOKEN"]).IsEqualTo("t");
        await Assert.That(store.List().Select(s => s.Name)).IsEquivalentTo(["a", "github"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(store.Delete("a")).IsTrue();
        await Assert.That(store.Delete("a")).IsFalse();
        await Assert.That(store.Find("a")).IsNull();
    }

    private static SessionRecord Session(Guid id) => new()
    {
        Id = id,
        Status = SessionStatus.Completed,
        Prompt = "p",
        Provider = "claude",
        Model = "sonnet",
        Caller = "tests",
        CreatedAt = DateTimeOffset.UnixEpoch,
        Tools = ["github"],
        Skills = [],
    };
}
