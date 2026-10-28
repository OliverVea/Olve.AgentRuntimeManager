using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Sessions;
using Olve.AgentRuntimeManager.Tools.Protocol;

namespace Olve.AgentRuntimeManager.UnitTests.Persistence;

/// <summary>
/// Sessions stored without tools (they ran on Claude Code's own, which can't be had any more since
/// 2026-10-27) get all of ARM's tools when the database is brought up to date; a session always
/// states its tools. Named ones stay.
/// </summary>
public sealed class EmptyToolsMigrationTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    [Test]
    public async Task ASessionStoredWithoutTools_GetsAllOfArmsTools_NamedToolsStay()
    {
        var (unset, named) = (Guid.NewGuid(), Guid.NewGuid());
        _database.Store().Add(Session(unset, []));
        _database.Store().Add(Session(named, ["arm_bash"]));
        await using (var db = _database.Context())
        {
            // Back to the database as it was then (Tools nullable), a session without, then up again.
            await db.GetService<IMigrator>().MigrateAsync("20261026131404_SessionTools");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Sessions SET Tools = NULL WHERE Id = {unset}");
            await db.Database.MigrateAsync();
        }

        // The tools ARM had on 2026-10-27, frozen in the migration (a later tool, arm_skill_load, is not added to old sessions).
        await Assert.That(_database.Store().Get(unset)!.Tools)
            .IsEquivalentTo([ToolCatalog.Bash, ToolCatalog.FileRead, ToolCatalog.FileEdit, ToolCatalog.FileCreate, ToolCatalog.FileDelete, ToolCatalog.FileCopy, ToolCatalog.FileMove], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(_database.Store().Get(named)!.Tools).IsEquivalentTo(["arm_bash"]);
    }

    private static SessionRecord Session(Guid id, IReadOnlyList<string> tools) => new()
    {
        Id = id,
        Status = SessionStatus.Completed,
        Prompt = "p",
        Provider = "claude",
        Model = "sonnet",
        Caller = "tests",
        CreatedAt = DateTimeOffset.UnixEpoch,
        Tools = tools,
        Skills = [],
    };
}
