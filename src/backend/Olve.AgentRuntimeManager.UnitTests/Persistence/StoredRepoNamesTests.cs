using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Persistence;
using Olve.AgentRuntimeManager.Sessions;
using Olve.AgentRuntimeManager.Sessions.Repos;

namespace Olve.AgentRuntimeManager.UnitTests.Persistence;

/// <summary>
/// Sessions stored before a repo kept its folder and branch (their <c>Repos</c> JSON has only
/// <c>Url</c> and <c>Ref</c>): bringing the database up to date at startup names them once.
/// </summary>
public sealed class StoredRepoNamesTests : IDisposable
{
    private const string OldFormat = """[{"Url":"git@github.com:OliverVea/Olve.Trains.git","Ref":"main"},{"Url":"https://github.com/OliverVea/Olve.Utilities/","Ref":null}]""";

    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    [Test]
    public async Task AnOldFormatSession_GetsItsFolderAndBranch_WhenTheDatabaseIsBroughtUpToDate()
    {
        var id = Guid.Parse("d3b07384-d9a0-4c9b-8f3e-0123456789ab");
        _database.Store().Add(Session(id));
        await using (var db = _database.Context())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Sessions SET Repos = {OldFormat} WHERE Id = {id}");
        }

        await using (var services = Services())
        {
            await services.MigrateDatabaseAsync();
        }

        var stored = _database.Store().Get(id)!;
        await Assert.That(stored.Repos).IsEquivalentTo([
            new RepoRecord("git@github.com:OliverVea/Olve.Trains.git", "main", "Olve.Trains", "arm/d3b07384"),
            new RepoRecord("https://github.com/OliverVea/Olve.Utilities/", null, "Olve.Utilities", "arm/d3b07384"),
        ]);
        await Assert.That(stored.ToDto(2).Repos!.Select(r => (r.Folder, r.Branch)))
            .IsEquivalentTo([("Olve.Trains", "arm/d3b07384"), ("Olve.Utilities", "arm/d3b07384")]);
        // Stored, not worked out on each read: the JSON itself has them now.
        await Assert.That(await ReposJson(id)).Contains("\"Folder\":\"Olve.Trains\"");
    }

    [Test]
    public async Task AnOldFormatWorktreeRemoval_GetsItsFolderAndBranch()
    {
        var id = Guid.Parse("0b7d3e52-91c4-4a8f-a6d0-5f2e8c1b7a93");
        _database.Removals().Save(new WorktreeRemovalRecord
        {
            SessionId = id, Provider = "claude", Repos = [RepoNames.Record(id, "https://x/a")], Attempts = 1, NextAttemptAt = DateTimeOffset.UnixEpoch,
        });
        await using (var db = _database.Context())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE WorktreeRemovals SET Repos = {OldFormat} WHERE SessionId = {id}");
        }

        await using (var db = _database.Context())
        {
            await Assert.That(await StoredRepoNames.FillAsync(db)).IsEqualTo(1);
        }

        await Assert.That(_database.Removals().Due(DateTimeOffset.UnixEpoch.AddDays(1)).Single().Repos.Select(r => (r.Folder, r.Branch)))
            .IsEquivalentTo([("Olve.Trains", "arm/0b7d3e52"), ("Olve.Utilities", "arm/0b7d3e52")]);
    }

    [Test]
    public async Task NamedSessions_AreLeftAsTheyAre()
    {
        var id = Guid.NewGuid();
        // A folder the URL alone wouldn't give: filling must not overwrite what's stored.
        var stored = new RepoRecord("https://x/a", null, "kept", "arm/kept");
        _database.Store().Add(Session(id) with { Repos = [stored] });
        _database.Store().Add(Session(Guid.NewGuid()));

        await using (var db = _database.Context())
        {
            await Assert.That(await StoredRepoNames.FillAsync(db)).IsEqualTo(0);
        }

        await Assert.That(_database.Store().Get(id)!.Repos).IsEquivalentTo([stored]);
    }

    private async Task<string?> ReposJson(Guid id)
    {
        await using var db = _database.Context();
        return await db.Database.SqlQuery<string?>($"SELECT Repos AS Value FROM Sessions WHERE Id = {id}").SingleAsync();
    }

    private ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<ArmDbContext>(options => options.UseSqlite(_database.ConnectionString));
        return services.BuildServiceProvider();
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
        Repos = [RepoNames.Record(id, "https://x/placeholder")],
    };
}
