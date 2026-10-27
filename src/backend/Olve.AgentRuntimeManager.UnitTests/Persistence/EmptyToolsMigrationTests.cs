using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Sessions;

namespace Olve.AgentRuntimeManager.UnitTests.Persistence;

/// <summary>
/// Sessions stored while an empty <c>tools</c> still meant the provider's built-ins (before
/// 2026-10-27) keep the built-ins: bringing the database up to date unsets it. Named tools stay.
/// </summary>
public sealed class EmptyToolsMigrationTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    [Test]
    public async Task AnEmptyListStoredBefore_BecomesUnset_NamedToolsStay()
    {
        var (empty, named) = (Guid.NewGuid(), Guid.NewGuid());
        _database.Store().Add(Session(empty, []));
        _database.Store().Add(Session(named, ["arm_bash"]));
        await using (var db = _database.Context())
        {
            // Back to the database as it was then (the migration's Down keeps the data), then up again.
            await db.GetService<IMigrator>().MigrateAsync("20261026131404_SessionTools");
            await Assert.That(await db.Database.SqlQuery<string>($"SELECT Tools AS Value FROM Sessions WHERE Id = {empty}").SingleAsync()).IsEqualTo("[]");
            await db.Database.MigrateAsync();
        }

        await Assert.That(_database.Store().Get(empty)!.Tools).IsNull();
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
    };
}
