using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Olve.AgentRuntimeManager.Sessions.Providers;
using Olve.AgentRuntimeManager.Sessions.Providers.Claude;
using Olve.AgentRuntimeManager.Sessions.Repos;
using Olve.AgentRuntimeManager.Sessions.Supervision;
using Olve.AgentRuntimeManager.Supervisor.Protocol;
using Olve.AgentRuntimeManager.UnitTests.Support;

namespace Olve.AgentRuntimeManager.UnitTests.Sessions.Claude;

/// <summary>
/// A session's repositories (M5e), checked out by the real supervisor before the stub CLI
/// (Stub/claude-stub.sh, which writes <c>invocation.txt</c> where it was started) runs. The
/// origins are local repositories, named by their paths: no network.
/// </summary>
public class ClaudeRepoTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private readonly string _root = Directory.CreateTempSubdirectory("arm-repos-").FullName;

    [After(Test)]
    public void Cleanup()
    {
        TestProcesses.KillSupervisors(_root);
        Directory.Delete(_root, recursive: true);
    }

    [Test]
    public async Task NoRepos_AgentStartsInTheWorkplace_AndNothingIsCloned()
    {
        var launch = Launch();

        var outcome = await Provider().Start(launch).Completion.WaitAsync(Guard);

        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Completed(0));
        await Assert.That(File.Exists(Path.Combine(Workplace(launch), "invocation.txt"))).IsTrue();
        await Assert.That(Directory.Exists(SharedRoot)).IsFalse();
    }

    [Test]
    public async Task OneRepo_IsAWorktreeOnTheSessionsBranch_AndTheAgentStartsInIt()
    {
        var origin = TestGit.Origin(Path.Combine(_root, "origin", "alpha"));
        var launch = Launch(new RepoRecord(origin));

        var outcome = await Provider().Start(launch).Completion.WaitAsync(Guard);

        var worktree = Path.Combine(Workplace(launch), "alpha");
        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Completed(0));
        await Assert.That(TestGit.Run(worktree, "rev-parse", "--abbrev-ref", "HEAD")).IsEqualTo($"arm/{launch.SessionId.ToString()[..8]}");
        await Assert.That(TestGit.Head(worktree)).IsEqualTo(TestGit.Head(origin));
        await Assert.That(File.Exists(Path.Combine(worktree, "README.md"))).IsTrue();
        await Assert.That((await Environment(worktree))["PWD"]).IsEqualTo(worktree);
        // A worktree of the shared bare copy, not a clone of its own.
        await Assert.That(File.Exists(Path.Combine(worktree, ".git"))).IsTrue();
        await Assert.That(Directory.GetDirectories(SharedRoot, "alpha-*.git")).Count().IsEqualTo(1);
    }

    [Test]
    public async Task TwoRepos_AreBothCheckedOut_AndTheAgentStartsInTheWorkplace()
    {
        var alpha = TestGit.Origin(Path.Combine(_root, "origin", "alpha"));
        var beta = TestGit.Origin(Path.Combine(_root, "origin", "beta.git"));
        var launch = Launch(new RepoRecord(alpha), new RepoRecord($"file://{beta}"));

        var outcome = await Provider().Start(launch).Completion.WaitAsync(Guard);

        var branch = $"arm/{launch.SessionId.ToString()[..8]}";
        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Completed(0));
        await Assert.That(TestGit.Run(Path.Combine(Workplace(launch), "alpha"), "rev-parse", "--abbrev-ref", "HEAD")).IsEqualTo(branch);
        await Assert.That(TestGit.Run(Path.Combine(Workplace(launch), "beta"), "rev-parse", "--abbrev-ref", "HEAD")).IsEqualTo(branch);
        await Assert.That((await Environment(Workplace(launch)))["PWD"]).IsEqualTo(Workplace(launch));
    }

    [Test]
    public async Task Ref_ABranch_StartsFromIt()
    {
        var origin = TestGit.Origin(Path.Combine(_root, "origin", "alpha"));
        TestGit.Run(origin, "checkout", "--quiet", "-b", "feature");
        var feature = TestGit.Commit(origin, "feature.txt", "feature");
        TestGit.Run(origin, "checkout", "--quiet", "main");
        TestGit.Commit(origin, "main.txt", "later on main");
        var launch = Launch(new RepoRecord(origin, "feature"));

        await Provider().Start(launch).Completion.WaitAsync(Guard);

        var worktree = Path.Combine(Workplace(launch), "alpha");
        await Assert.That(TestGit.Head(worktree)).IsEqualTo(feature);
        await Assert.That(TestGit.Run(worktree, "rev-parse", "--abbrev-ref", "HEAD")).IsEqualTo($"arm/{launch.SessionId.ToString()[..8]}");
    }

    [Test]
    public async Task Ref_ACommit_StartsFromIt()
    {
        var origin = TestGit.Origin(Path.Combine(_root, "origin", "alpha"));
        var first = TestGit.Head(origin);
        TestGit.Commit(origin, "second.txt", "second");
        var launch = Launch(new RepoRecord(origin, first));

        var outcome = await Provider().Start(launch).Completion.WaitAsync(Guard);

        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Completed(0));
        await Assert.That(TestGit.Head(Path.Combine(Workplace(launch), "alpha"))).IsEqualTo(first);
    }

    [Test]
    public async Task Ref_ATag_StartsFromIt()
    {
        var origin = TestGit.Origin(Path.Combine(_root, "origin", "alpha"));
        var tagged = TestGit.Head(origin);
        TestGit.Run(origin, "tag", "v1");
        TestGit.Commit(origin, "second.txt", "second");
        var launch = Launch(new RepoRecord(origin, "v1"));

        await Provider().Start(launch).Completion.WaitAsync(Guard);

        await Assert.That(TestGit.Head(Path.Combine(Workplace(launch), "alpha"))).IsEqualTo(tagged);
    }

    [Test]
    public async Task UnknownRef_FailsTheSession_WithWhy_AndTheAgentNeverStarts()
    {
        var origin = TestGit.Origin(Path.Combine(_root, "origin", "alpha"));
        var launch = Launch(new RepoRecord(origin, "no-such-ref"));

        var outcome = await Provider().Start(launch).Completion.WaitAsync(Guard);

        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Failed($"Checking out {origin} failed: 'no-such-ref' is not a branch, tag or commit of it."));
        await Assert.That(AgentStarted(launch)).IsFalse();
        await Assert.That(Directory.Exists(Path.Combine(Workplace(launch), "alpha"))).IsFalse();
    }

    [Test]
    public async Task NoSuchRepository_FailsTheSession_WithGitsError()
    {
        var missing = Path.Combine(_root, "origin", "missing");
        var launch = Launch(new RepoRecord(missing));

        var outcome = await Provider().Start(launch).Completion.WaitAsync(Guard);

        await Assert.That(outcome).IsTypeOf<AgentOutcome.Failed>();
        var error = ((AgentOutcome.Failed)outcome).Error;
        await Assert.That(error).StartsWith($"Checking out {missing} failed: ");
        await Assert.That(error).Contains("does not appear to be a git repository");
        await Assert.That(AgentStarted(launch)).IsFalse();
        // A failed clone leaves no shared copy behind (the next session tries afresh).
        await Assert.That(Directory.GetDirectories(SharedRoot)).IsEmpty();
    }

    [Test]
    public async Task HangingGit_IsKilledAfterTheTimeout_AndFailsTheSession()
    {
        TestGit.Require();
        // ssh that never answers: git runs GIT_SSH_COMMAND through the shell, so this sleeps.
        var launch = Launch(new RepoRecord("ssh://arm.invalid/hangs.git")) with
        {
            Env = new Dictionary<string, string> { ["GIT_SSH_COMMAND"] = "sleep 60;:" },
        };

        var outcome = await Provider(gitTimeout: TimeSpan.FromSeconds(1)).Start(launch).Completion.WaitAsync(Guard);

        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Failed("Checking out ssh://arm.invalid/hangs.git failed: git fetch took more than 1 s."));
        await Assert.That(AgentStarted(launch)).IsFalse();
    }

    [Test]
    public async Task TwoSessions_OnOneRepository_AtOnce_EachGetsItsOwnWorktreeAndBranch()
    {
        var origin = TestGit.Origin(Path.Combine(_root, "origin", "alpha"));
        var provider = Provider();
        var first = Launch(new RepoRecord(origin));
        var second = Launch(new RepoRecord(origin));

        var runs = new[] { provider.Start(first), provider.Start(second) };
        var outcomes = await Task.WhenAll(runs.Select(r => r.Completion)).WaitAsync(Guard);

        await Assert.That(outcomes.All(o => o == new AgentOutcome.Completed(0))).IsTrue();
        var branches = new[] { first, second }.Select(l => TestGit.Run(Path.Combine(Workplace(l), "alpha"), "rev-parse", "--abbrev-ref", "HEAD")).ToList();
        await Assert.That(branches).IsEquivalentTo([$"arm/{first.SessionId.ToString()[..8]}", $"arm/{second.SessionId.ToString()[..8]}"]);
        await Assert.That(Directory.GetDirectories(SharedRoot, "alpha-*.git")).Count().IsEqualTo(1);
    }

    [Test]
    public async Task ASecondSession_SeesWhatWasPushedAfterTheFirstClone()
    {
        var origin = TestGit.Origin(Path.Combine(_root, "origin", "alpha"));
        var provider = Provider();
        var first = Launch(new RepoRecord(origin));
        await provider.Start(first).Completion.WaitAsync(Guard);
        var pushed = TestGit.Commit(origin, "later.txt", "pushed later");

        var second = Launch(new RepoRecord(origin));
        await provider.Start(second).Completion.WaitAsync(Guard);

        await Assert.That(TestGit.Head(Path.Combine(Workplace(second), "alpha"))).IsEqualTo(pushed);
        await Assert.That(TestGit.Head(Path.Combine(Workplace(first), "alpha"))).IsNotEqualTo(pushed);
    }

    [Test]
    public async Task Continuing_KeepsTheWorktree_AndItsUncommittedWork_AndFetchesNothing()
    {
        var origin = TestGit.Origin(Path.Combine(_root, "origin", "alpha"));
        var provider = Provider();
        var launch = Launch(new RepoRecord(origin));
        var run = provider.Start(launch);
        await run.Completion.WaitAsync(Guard);
        var worktree = Path.Combine(Workplace(launch), "alpha");
        var started = TestGit.Head(worktree);
        await File.WriteAllTextAsync(Path.Combine(worktree, "notes.txt"), "uncommitted");
        var pushed = TestGit.Commit(origin, "later.txt", "pushed later");

        var continued = launch with { Attempt = 2, RunId = Guid.NewGuid(), Resume = run.ProviderSessionId, Messages = ["carry on"] };
        var outcome = await provider.Start(continued).Completion.WaitAsync(Guard);

        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Completed(0));
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(worktree, "notes.txt"))).IsEqualTo("uncommitted");
        await Assert.That(TestGit.Head(worktree)).IsEqualTo(started);
        // Not fetched: the shared copy hasn't seen the later commit.
        await Assert.That(TestGit.TryRun(Shared("alpha"), "cat-file", "-e", $"{pushed}^{{commit}}").ExitCode).IsNotEqualTo(0);
        await Assert.That((await Environment(worktree))["PWD"]).IsEqualTo(worktree);
    }

    [Test]
    public async Task Delete_RemovesTheWorktree_AndKeepsTheBranch()
    {
        var origin = TestGit.Origin(Path.Combine(_root, "origin", "alpha"));
        var provider = Provider();
        var launch = Launch(new RepoRecord(origin));
        await provider.Start(launch).Completion.WaitAsync(Guard);
        var worktree = Path.Combine(Workplace(launch), "alpha");
        await File.WriteAllTextAsync(Path.Combine(worktree, "notes.txt"), "uncommitted");

        provider.RemoveRepos(launch.SessionId, launch.Repos!);

        var branch = $"arm/{launch.SessionId.ToString()[..8]}";
        await Assert.That(Directory.Exists(worktree)).IsFalse();
        await Assert.That(TestGit.Run(Shared("alpha"), "worktree", "list", "--porcelain")).DoesNotContain(worktree);
        await Assert.That(TestGit.TryRun(Shared("alpha"), "show-ref", "--verify", "--quiet", $"refs/heads/{branch}").ExitCode).IsEqualTo(0);
    }

    [Test]
    public async Task Delete_AWorktreeThatsGoneAlready_IsFine()
    {
        var origin = TestGit.Origin(Path.Combine(_root, "origin", "alpha"));
        var provider = Provider();
        var launch = Launch(new RepoRecord(origin));
        await provider.Start(launch).Completion.WaitAsync(Guard);
        var worktree = Path.Combine(Workplace(launch), "alpha");
        Directory.Delete(worktree, recursive: true);

        provider.RemoveRepos(launch.SessionId, launch.Repos!);

        await Assert.That(TestGit.Run(Shared("alpha"), "worktree", "list", "--porcelain")).DoesNotContain(worktree);
    }

    [Test]
    public async Task ContinuingAfterTheWorktreeWasRemoved_ChecksOutTheSessionsBranchAgain()
    {
        var origin = TestGit.Origin(Path.Combine(_root, "origin", "alpha"));
        var provider = Provider();
        var launch = Launch(new RepoRecord(origin));
        var run = provider.Start(launch);
        await run.Completion.WaitAsync(Guard);
        var worktree = Path.Combine(Workplace(launch), "alpha");
        TestGit.Run(worktree, "add", "invocation.txt");
        TestGit.Run(worktree, "commit", "--quiet", "-m", "the agent's work");
        var committed = TestGit.Head(worktree);
        Directory.Delete(worktree, recursive: true);

        var continued = launch with { Attempt = 2, RunId = Guid.NewGuid(), Resume = run.ProviderSessionId, Messages = ["carry on"] };
        var outcome = await provider.Start(continued).Completion.WaitAsync(Guard);

        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Completed(0));
        await Assert.That(TestGit.Head(worktree)).IsEqualTo(committed);
    }

    private string SharedRoot => Path.Combine(_root, "repos");

    private string Shared(string name) => Directory.GetDirectories(SharedRoot, $"{name}-*.git").Single();

    private ClaudeProvider Provider(TimeSpan? gitTimeout = null)
    {
        var supervisors = new Supervisors(
            Options.Create(new SupervisorOptions { SocketRoot = Path.Combine(_root, "sockets") }), NullLogger<Supervisors>.Instance);
        var repos = new RepoCheckouts(
            Options.Create(new RepoOptions { Root = SharedRoot, GitTimeout = gitTimeout ?? TimeSpan.FromSeconds(20) }), supervisors, NullLogger<RepoCheckouts>.Instance);
        var options = new ClaudeProviderOptions { Command = ClaudeProviderTests.Stub, WorkRoot = Path.Combine(_root, "sessions") };
        return new ClaudeProvider(Options.Create(options), supervisors, repos, NullLogger<ClaudeProvider>.Instance);
    }

    private static AgentLaunch Launch(params RepoRecord[] repos)
    {
        var id = Guid.NewGuid();
        return new(id, "Say READY", "sonnet", 1, id, Guid.NewGuid(), Repos: repos);
    }

    private string Workplace(AgentLaunch launch) => Path.Combine(_root, "sessions", launch.SessionId.ToString(), "work");

    /// <summary>Whether the stub CLI ran: it writes <c>invocation.txt</c> wherever it starts, and output.</summary>
    private bool AgentStarted(AgentLaunch launch)
    {
        var folder = Path.Combine(_root, "sessions", launch.SessionId.ToString());
        return Directory.EnumerateFiles(folder, "invocation.txt", SearchOption.AllDirectories).Any()
            || SupervisorFiles.ReadInfo(folder) is not null;
    }

    /// <summary>The environment the stub CLI was started with, from the <c>invocation.txt</c> in <paramref name="directory"/>.</summary>
    private static async Task<Dictionary<string, string>> Environment(string directory) =>
        (await File.ReadAllLinesAsync(Path.Combine(directory, "invocation.txt")))
            .Where(l => l.StartsWith("env:", StringComparison.Ordinal))
            .Select(l => l[4..].Split('=', 2))
            .Where(pair => pair.Length == 2)
            .ToDictionary(pair => pair[0], pair => pair[1]);
}
