using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Sessions;
using Olve.AgentRuntimeManager.Sessions.Skills;

namespace Olve.AgentRuntimeManager.UnitTests.Persistence;

/// <summary>
/// The skills migration (M10b): sessions stored before it have no skills (<c>[]</c>, never null:
/// a session always states its skills), and the registry's table is there.
/// </summary>
public sealed class SkillsMigrationTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    [Test]
    public async Task ASessionFromBeforeSkills_HasNone()
    {
        var id = Guid.NewGuid();
        _database.Store().Add(Session(id));
        await using (var db = _database.Context())
        {
            // Back to the database as it was then (no Skills column; the row stays), then up again.
            await db.GetService<IMigrator>().MigrateAsync("20261027141538_SessionToolsRequired");
            await db.Database.MigrateAsync();
        }

        await Assert.That(_database.Store().Get(id)!.Skills).IsEquivalentTo(Array.Empty<string>());
        await Assert.That(_database.Store().Get(id)!.Tools).IsEquivalentTo(["arm_bash"]);
    }

    [Test]
    public async Task TheRegistry_KeepsASkill_WithItsSource()
    {
        var store = _database.Skills();
        var skill = new SkillRecord
        {
            Name = "pdf", Description = "Reads PDFs.", IsDefault = true, SourceUrl = "https://example.invalid/skills.git", SourceRef = "v1", SourcePath = "skills/pdf",
            UpdatedAt = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero),
        };

        store.Set(skill);
        store.Set(new SkillRecord { Name = "a", Description = "First by name.", IsDefault = false, SourceUrl = "https://example.invalid/a.git", UpdatedAt = skill.UpdatedAt });

        await Assert.That(store.Find("pdf")).IsEqualTo(skill);
        await Assert.That(store.List().Select(s => s.Name)).IsEquivalentTo(["a", "pdf"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(store.Delete("pdf")).IsTrue();
        await Assert.That(store.Delete("pdf")).IsFalse();
        await Assert.That(store.Find("pdf")).IsNull();
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
        Tools = ["arm_bash"],
        Skills = ["pdf"],
    };
}
