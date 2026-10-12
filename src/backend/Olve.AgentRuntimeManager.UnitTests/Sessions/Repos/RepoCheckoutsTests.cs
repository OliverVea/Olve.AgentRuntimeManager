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
    [Arguments("../../outside")]
    [Arguments("..")]
    [Arguments(".")]
    [Arguments("/etc")]
    [Arguments("a/b")]
    [Arguments("a\\b")]
    [Arguments("")]
    [Arguments(null)]
    public async Task Plan_AndRemove_RefuseAStoredFolderThatIsNotOneFolderNameInTheWorkplace(string? folder)
    {
        var checkouts = Checkouts();
        RepoRecord[] repos = [new("https://x/a.git", null, folder!, "arm/stored")];

        var plan = Assert.Throws<InvalidOperationException>(() => checkouts.Plan(Guid.NewGuid(), "/w/s/work", repos));
        // Removal plans first, so it touches nothing either.
        var removal = Assert.Throws<InvalidOperationException>(() => checkouts.Remove(Guid.NewGuid(), "/w/s/work", repos));

        await Assert.That(plan!.Message).IsEqualTo($"Checking out https://x/a.git failed: its stored folder '{folder}' isn't a folder name in the session's workplace.");
        await Assert.That(removal).IsNotNull();
    }

    [Test]
    public async Task Plan_RefusesAMissingBranch_AndTakesAPlainFolderLikeDots()
    {
        var checkouts = Checkouts();

        var missing = Assert.Throws<InvalidOperationException>(() => checkouts.Plan(Guid.NewGuid(), "/w", [new("https://x/a.git", null, "a", null!)]));
        var dots = checkouts.Plan(Guid.NewGuid(), "/w", [new("https://x/....git", null, "...", "arm/x")]).Single();

        await Assert.That(missing!.Message).IsEqualTo("Checking out https://x/a.git failed: it has no branch stored.");
        await Assert.That(dots.Path).IsEqualTo("/w/...");
    }

    private static RepoCheckouts Checkouts()
    {
        var root = Path.Combine(Path.GetTempPath(), "arm-plan-test");
        var supervisors = new Supervisors(Options.Create(new SupervisorOptions { SocketRoot = root }), NullLogger<Supervisors>.Instance);
        return new RepoCheckouts(Options.Create(new RepoOptions { Root = root }), supervisors, NullLogger<RepoCheckouts>.Instance);
    }

    [Test]
    public async Task Record_NamesTheFolderAndBranch_AsANewSessionsRepos()
    {
        var id = Guid.Parse("d3b07384-d9a0-4c9b-8f3e-0123456789ab");

        await Assert.That(RepoNames.Record(id, "git@github.com:o/Olve.Trains.git", "v1"))
            .IsEqualTo(new RepoRecord("git@github.com:o/Olve.Trains.git", "v1", "Olve.Trains", "arm/d3b07384"));
    }
}
