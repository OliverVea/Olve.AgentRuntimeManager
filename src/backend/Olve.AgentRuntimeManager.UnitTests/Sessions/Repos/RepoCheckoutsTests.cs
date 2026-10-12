using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Olve.AgentRuntimeManager.Sessions.Repos;
using Olve.AgentRuntimeManager.Sessions.Supervision;

namespace Olve.AgentRuntimeManager.UnitTests.Sessions.Repos;

/// <summary>What a session's checkout is planned as: the folder and branch it was stored with.</summary>
public class RepoCheckoutsTests
{
    [Test]
    public async Task Plan_UsesTheStoredFolderAndBranch()
    {
        var id = Guid.NewGuid();
        var root = Path.Combine(Path.GetTempPath(), "arm-plan-test");
        var supervisors = new Supervisors(Options.Create(new SupervisorOptions { SocketRoot = root }), NullLogger<Supervisors>.Instance);
        var checkouts = new RepoCheckouts(Options.Create(new RepoOptions { Root = root }), supervisors, NullLogger<RepoCheckouts>.Instance);
        // Not what the URL and id alone would give: the plan must take what's stored, not name them again.
        RepoRecord[] repos = [new("https://x/a.git", "main", "stored-folder", "arm/stored")];

        var plan = checkouts.Plan(id, "/w", repos).Single();

        await Assert.That(plan.Path).IsEqualTo(Path.Combine("/w", "stored-folder"));
        await Assert.That(plan.Branch).IsEqualTo("arm/stored");
        await Assert.That(plan.Ref).IsEqualTo("main");
        await Assert.That(plan.Owner).IsEqualTo(id.ToString());
    }

    [Test]
    public async Task Record_NamesTheFolderAndBranch_AsANewSessionsRepos()
    {
        var id = Guid.Parse("d3b07384-d9a0-4c9b-8f3e-0123456789ab");

        await Assert.That(RepoNames.Record(id, "git@github.com:o/Olve.Trains.git", "v1"))
            .IsEqualTo(new RepoRecord("git@github.com:o/Olve.Trains.git", "v1", "Olve.Trains", "arm/d3b07384"));
    }
}
