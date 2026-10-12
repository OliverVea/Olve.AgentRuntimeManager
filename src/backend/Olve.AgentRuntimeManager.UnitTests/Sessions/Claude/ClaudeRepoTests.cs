using System.Runtime.Versioning;
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
[UnsupportedOSPlatform("windows")]
public class ClaudeRepoTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    /// <summary>How soon a kill must have stopped a checkout: well inside the git timeout (20 s here), so a git that ran into it doesn't pass.</summary>
    private static readonly TimeSpan Promptly = TimeSpan.FromSeconds(10);

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
    public async Task OneRepo_IsAWorktreeOnTheSessionsBranch_AndTheAgentStartsInTheWorkplace()
    {
        var origin = TestGit.Origin(Path.Combine(_root, "origin", "alpha"));
        var launch = Launch(new RepoRecord(origin));

        var outcome = await Provider().Start(launch).Completion.WaitAsync(Guard);

        var worktree = Path.Combine(Workplace(launch), "alpha");
        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Completed(0));
        await Assert.That(TestGit.Run(worktree, "rev-parse", "--abbrev-ref", "HEAD")).IsEqualTo($"arm/{launch.SessionId.ToString()[..8]}");
        await Assert.That(TestGit.Head(worktree)).IsEqualTo(TestGit.Head(origin));
        await Assert.That(File.Exists(Path.Combine(worktree, "README.md"))).IsTrue();
        await Assert.That((await Environment(Workplace(launch)))["PWD"]).IsEqualTo(Workplace(launch));
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
        var sleeper = $"sleep 60.{Random.Shared.Next(100_000, 999_999)}";
        var launch = Launch(new RepoRecord("ssh://arm.invalid/hangs.git")) with
        {
            Env = new Dictionary<string, string> { ["GIT_SSH_COMMAND"] = $"{sleeper};:" },
        };

        try
        {
            var outcome = await Provider(gitTimeout: TimeSpan.FromSeconds(1)).Start(launch).Completion.WaitAsync(Guard);

            await Assert.That(outcome).IsEqualTo(new AgentOutcome.Failed("Checking out ssh://arm.invalid/hangs.git failed: git fetch took more than 1 s."));
            await Assert.That(AgentStarted(launch)).IsFalse();
            // git's child (the "ssh") was ended with it, not left behind.
            await Assert.That(TestProcesses.WithCommandLine(sleeper)).IsEmpty();
        }
        finally
        {
            TestProcesses.KillWithCommandLine(sleeper);
        }
    }

    [Test]
    public async Task GitLeavingAProcessOnItsPipes_DoesntHoldTheCheckout()
    {
        TestGit.Require();
        // An "ssh" that fails at once but leaves a process holding git's stderr, not its protocol (like an ssh
        // ControlMaster with ControlPersist): git exits, its pipes stay open.
        var lingerer = $"sleep 59.{Random.Shared.Next(100_000, 999_999)}";
        var launch = Launch(new RepoRecord("ssh://arm.invalid/lingers.git")) with
        {
            Env = new Dictionary<string, string> { ["GIT_SSH_COMMAND"] = $"{lingerer} >/dev/null & echo 'no route to arm.invalid' >&2; exit 255;:" },
        };

        try
        {
            var outcome = await Provider().Start(launch).Completion.WaitAsync(TimeSpan.FromSeconds(20));

            await Assert.That(outcome).IsTypeOf<AgentOutcome.Failed>();
            await Assert.That(((AgentOutcome.Failed)outcome).Error).Contains("no route to arm.invalid");
        }
        finally
        {
            TestProcesses.KillWithCommandLine(lingerer);
        }
    }

    [Test]
    public async Task Kill_DuringTheCheckout_StopsIt_TheAgentNeverStarts_AndNothingIsLeftHalfMade()
    {
        var origin = TestGit.Origin(Path.Combine(_root, "origin", "alpha"));
        var gate = new GatedSsh(_root);
        var launch = Launch(new RepoRecord(GatedSsh.Url(origin))) with { Env = gate.Env };
        var run = Provider().Start(launch);
        await gate.ReachedAsync(Guard);

        run.Kill();
        var outcome = await run.Completion.WaitAsync(Promptly);

        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Killed());
        await Assert.That(AgentStarted(launch)).IsFalse();
        await Assert.That(run.AgentStarted).IsFalse();
        await Until(() => SupervisorFiles.ReadExit(Folder(launch)) is not null);
        await Assert.That(SupervisorFiles.ReadExit(Folder(launch))!.Error).IsEqualTo("Killed before its agent started.");
        // The clone it was in is gone (it was the first), so are the worktree and the waiting "ssh".
        // (The lock file stays: it's only ever locked, never removed.)
        await Assert.That(Directory.GetDirectories(SharedRoot)).IsEmpty();
        await Assert.That(Directory.GetFileSystemEntries(Workplace(launch))).IsEmpty();
        await Until(() => TestProcesses.WithCommandLine(gate.Fragment).Count == 0);
    }

    [Test]
    public async Task AKillMark_StopsTheCheckout_WithoutTheKillMessage_AndTheAgentNeverStarts()
    {
        // What a kill given before ARM is connected to the supervisor leaves: the mark only.
        var origin = TestGit.Origin(Path.Combine(_root, "origin", "alpha"));
        var gate = new GatedSsh(_root);
        var launch = Launch(new RepoRecord(GatedSsh.Url(origin))) with { Env = gate.Env };
        var run = Provider().Start(launch);
        await gate.ReachedAsync(Guard);

        await Assert.That(Posix.CreateMark(Path.Combine(Folder(launch), SupervisorFiles.KillMark(launch.RunId)))).IsTrue();
        var outcome = await run.Completion.WaitAsync(Promptly);

        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Failed("Killed before its agent started."));
        await Assert.That(AgentStarted(launch)).IsFalse();
        await Until(() => TestProcesses.WithCommandLine(gate.Fragment).Count == 0);
    }

    [Test]
    public async Task ACheckoutThatFails_EndsTheRunOnce_EvenIfItsSocketIsGoneMeanwhile()
    {
        TestGit.Require();
        var gate = new GatedSsh(_root);
        var missing = Path.Combine(_root, "origin", "missing");
        var launch = Launch(new RepoRecord(GatedSsh.Url(missing))) with { Env = gate.Env };
        var run = Provider().Start(launch);
        await gate.ReachedAsync(Guard);
        var supervisors = TestProcesses.WithCommandLine("olve-arm-supervisor").Where(pid => IsIn(pid, Folder(launch))).ToList();

        // Its socket's folder goes (as when a test cleans up, or an admin clears /run) while it checks out.
        Directory.Delete(Path.Combine(_root, "sockets"), recursive: true);
        gate.Release();
        var outcome = await run.Completion.WaitAsync(Guard);
        await Until(() => supervisors.All(pid => Posix.StartTime(pid) is null));

        await Assert.That(outcome).IsTypeOf<AgentOutcome.Failed>();
        await Assert.That(((AgentOutcome.Failed)outcome).Error).StartsWith($"Checking out {GatedSsh.Url(missing)} failed: ");
        await Assert.That(Log(launch)).DoesNotContain("could not start the agent");
    }

    [Test]
    public async Task Kill_DuringAFetch_LeavesTheSharedCopyAndNoBranch()
    {
        var origin = TestGit.Origin(Path.Combine(_root, "origin", "alpha"));
        var gate = new GatedSsh(_root);
        var provider = Provider();
        var url = GatedSsh.Url(origin);
        gate.Release();
        await provider.Start(Launch(new RepoRecord(url)) with { Env = gate.Env }).Completion.WaitAsync(Guard);
        var slow = new GatedSsh(_root);
        var launch = Launch(new RepoRecord(url)) with { Env = slow.Env };
        var run = provider.Start(launch);
        await slow.ReachedAsync(Guard);

        run.Kill();
        var outcome = await run.Completion.WaitAsync(Promptly);

        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Killed());
        await Assert.That(AgentStarted(launch)).IsFalse();
        await Assert.That(TestGit.TryRun(Shared("alpha"), "show-ref", "--verify", "--quiet", $"refs/heads/{Branch(launch)}").ExitCode).IsNotEqualTo(0);
        await Assert.That(Directory.GetFileSystemEntries(Workplace(launch))).IsEmpty();
    }

    [Test]
    public async Task ServerRestart_DuringTheCheckout_ReattachesAndTheRunCompletes()
    {
        var origin = TestGit.Origin(Path.Combine(_root, "origin", "alpha"));
        var gate = new GatedSsh(_root);
        var launch = Launch(new RepoRecord(GatedSsh.Url(origin))) with { Env = gate.Env };
        var (before, provider) = Server();
        _ = provider.Start(launch);
        await gate.ReachedAsync(Guard);

        // ARM dies: its connections close, the supervisor (checking out) stays.
        before.Dispose();
        var (_, after) = Server();
        var recovered = after.Recover(new AgentRecovery(launch.SessionId, launch.Prompt, launch.Model, launch.SessionId.ToString(),
            launch.RunId, Guid.NewGuid(), launch.Env, launch.Repos));
        gate.Release();
        var outcome = await recovered!.Run.Completion.WaitAsync(Guard);

        var worktree = Path.Combine(Workplace(launch), "alpha");
        await Assert.That(recovered.Resumed).IsFalse();
        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Completed(0));
        await Assert.That((await Environment(Workplace(launch)))["PWD"]).IsEqualTo(Workplace(launch));
    }

    [Test]
    public async Task Continuing_WithTheWorkRootBehindALink_KeepsTheWorktree()
    {
        var origin = TestGit.Origin(Path.Combine(_root, "origin", "alpha"));
        var real = Directory.CreateDirectory(Path.Combine(_root, "real-sessions")).FullName;
        var linked = Path.Combine(_root, "linked-sessions");
        Directory.CreateSymbolicLink(linked, real);
        var provider = Provider(workRoot: linked);
        var launch = Launch(new RepoRecord(origin));
        var run = provider.Start(launch);
        await run.Completion.WaitAsync(Guard);
        var worktree = Path.Combine(linked, launch.SessionId.ToString(), "work", "alpha");
        await File.WriteAllTextAsync(Path.Combine(worktree, "notes.txt"), "uncommitted");

        var continued = launch with { Attempt = 2, RunId = Guid.NewGuid(), Resume = run.ProviderSessionId, Messages = ["carry on"] };
        var outcome = await provider.Start(continued).Completion.WaitAsync(Guard);

        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Completed(0));
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(worktree, "notes.txt"))).IsEqualTo("uncommitted");
    }

    [Test]
    public async Task SupervisorLost_DuringAContinuedRunsCheckout_ResumesWithItsMessages()
    {
        var origin = TestGit.Origin(Path.Combine(_root, "origin", "alpha"));
        var first = new GatedSsh(_root);
        first.Release();
        var launch = Launch(new RepoRecord(GatedSsh.Url(origin))) with { Env = first.Env };
        var (_, provider) = Server();
        var run = provider.Start(launch);
        await run.Completion.WaitAsync(Guard);
        // Its worktree gone, the session continues: the new run checks out again (held at the gate).
        Directory.Delete(Path.Combine(Workplace(launch), "alpha"), recursive: true);
        var gate = new GatedSsh(_root);
        var continued = launch with { Attempt = 2, RunId = Guid.NewGuid(), Resume = run.ProviderSessionId, Messages = ["carry on"], Env = gate.Env };
        var (before, again) = Server();
        _ = again.Start(continued);
        await gate.ReachedAsync(Guard);

        // ARM and the supervisor (with its git) die mid-checkout: this run's agent never started,
        // but the Claude session it resumes exists.
        before.Dispose();
        var supervisors = TestProcesses.WithCommandLine("olve-arm-supervisor").Where(pid => IsIn(pid, Folder(launch))).ToList();
        foreach (var supervisor in supervisors)
        {
            Posix.Kill(-supervisor, Posix.SigKill);
            Posix.Kill(supervisor, Posix.SigKill);
        }

        await Until(() => !before.IsListening(launch.SessionId));
        gate.Release();
        var (_, after) = Server();
        var recovered = after.Recover(new AgentRecovery(launch.SessionId, launch.Prompt, launch.Model, run.ProviderSessionId,
            continued.RunId, Guid.NewGuid(), continued.Env, launch.Repos, Resume: run.ProviderSessionId, Messages: ["carry on"]));
        var outcome = await recovered!.Run.Completion.WaitAsync(Guard);

        var arguments = await Arguments(Workplace(launch));
        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Completed(0));
        await Assert.That(arguments).Contains("--resume");
        await Assert.That(arguments).Contains(run.ProviderSessionId);
        await Assert.That(arguments).DoesNotContain("--session-id");
        var told = await File.ReadAllLinesAsync(Path.Combine(Workplace(launch), "prompt.jsonl"));
        await Assert.That(ClaudeProviderTests.Told(told[^1])).IsEqualTo("carry on");
    }

    [Test]
    public async Task ARunSupersededByANewerOne_DoesntWriteTheSessionsExit()
    {
        var origin = TestGit.Origin(Path.Combine(_root, "origin", "alpha"));
        var older = new GatedSsh(_root);
        var newer = new GatedSsh(_root);
        var launch = Launch(new RepoRecord(GatedSsh.Url(origin))) with { Env = older.Env };
        var provider = Provider();
        _ = provider.Start(launch);
        await older.ReachedAsync(Guard);
        // A newer run of the session (as after a kill the older one didn't get, then a continue).
        var next = launch with { Attempt = 2, RunId = Guid.NewGuid(), Env = newer.Env };
        var run = provider.Start(next);
        await Until(() => Log(launch).Contains("waiting for", StringComparison.Ordinal));

        older.Release();
        await Until(() => Log(launch).Contains("not writing exit.json", StringComparison.Ordinal));
        var exitMeanwhile = SupervisorFiles.ReadExit(Folder(launch));
        newer.Release();
        var outcome = await run.Completion.WaitAsync(Guard);

        await Assert.That(exitMeanwhile).IsNull();
        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Completed(0));
        await Assert.That(SupervisorFiles.ReadExit(Folder(launch))!.RunId).IsEqualTo(next.RunId);
        await Assert.That(Log(launch).Split('\n').Count(l => l.Contains(": agent ", StringComparison.Ordinal) && l.EndsWith(" started", StringComparison.Ordinal))).IsEqualTo(1);
    }

    [Test]
    public async Task ACheckoutPastItsLimit_IsStopped_AndFailsTheSession()
    {
        var origin = TestGit.Origin(Path.Combine(_root, "origin", "alpha"));
        var gate = new GatedSsh(_root);
        var url = GatedSsh.Url(origin);
        var launch = Launch(new RepoRecord(url)) with { Env = gate.Env };

        var outcome = await Provider(checkoutTimeout: TimeSpan.FromSeconds(2)).Start(launch).Completion.WaitAsync(Promptly);

        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Failed($"Checking out {url} failed: the checkout took more than 2 s."));
        await Assert.That(AgentStarted(launch)).IsFalse();
        await Until(() => TestProcesses.WithCommandLine(gate.Fragment).Count == 0);
    }

    [Test]
    public async Task SupervisorLost_DuringTheCheckout_StartsTheAgentAfresh_NotResumed()
    {
        var origin = TestGit.Origin(Path.Combine(_root, "origin", "alpha"));
        var gate = new GatedSsh(_root);
        var launch = Launch(new RepoRecord(GatedSsh.Url(origin))) with { Env = gate.Env };
        var (before, provider) = Server();
        _ = provider.Start(launch);
        await gate.ReachedAsync(Guard);

        // ARM and the supervisor (with its git) die mid-checkout: no agent ever ran.
        before.Dispose();
        var supervisors = TestProcesses.WithCommandLine("olve-arm-supervisor").Where(pid => IsIn(pid, Folder(launch))).ToList();
        foreach (var supervisor in supervisors)
        {
            Posix.Kill(-supervisor, Posix.SigKill);
            Posix.Kill(supervisor, Posix.SigKill);
        }

        // Gone, not just signalled: a dying one (its other threads exiting) may still take a connection.
        await Until(() => !before.IsListening(launch.SessionId));

        gate.Release();
        var (_, after) = Server();
        var recovered = after.Recover(new AgentRecovery(launch.SessionId, launch.Prompt, launch.Model, launch.SessionId.ToString(),
            launch.RunId, Guid.NewGuid(), launch.Env, launch.Repos));
        var outcome = await recovered!.Run.Completion.WaitAsync(Guard);

        var worktree = Path.Combine(Workplace(launch), "alpha");
        var arguments = await Arguments(Workplace(launch));
        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Completed(0));
        await Assert.That(arguments).Contains("--session-id");
        await Assert.That(arguments).DoesNotContain("--resume");
        await Assert.That(ClaudeProviderTests.Told((await File.ReadAllLinesAsync(Path.Combine(Workplace(launch), "input.jsonl")))[0])).IsEqualTo(launch.Prompt);
    }

    [Test]
    public async Task ASecondSession_WaitsForTheFirstsCheckout_ThenBothHaveTheirWorktrees()
    {
        var origin = TestGit.Origin(Path.Combine(_root, "origin", "alpha"));
        var gate = new GatedSsh(_root);
        var url = GatedSsh.Url(origin);
        var provider = Provider();
        var first = Launch(new RepoRecord(url)) with { Env = gate.Env };
        var firstRun = provider.Start(first);
        await gate.ReachedAsync(Guard);
        var second = Launch(new RepoRecord(url)) with { Env = gate.Env };

        var secondRun = provider.Start(second);
        await Until(() => Log(second).Contains("waiting for", StringComparison.Ordinal));
        var secondStartedEarly = AgentStarted(second);
        gate.Release();
        var outcomes = await Task.WhenAll(firstRun.Completion, secondRun.Completion).WaitAsync(Guard);

        await Assert.That(secondStartedEarly).IsFalse();
        await Assert.That(outcomes.All(o => o == new AgentOutcome.Completed(0))).IsTrue();
        await Assert.That(Directory.Exists(Path.Combine(Workplace(second), "alpha"))).IsTrue();
    }

    [Test]
    public async Task ALockThatIsNeverReleased_FailsTheCheckout_AfterItsWait()
    {
        var origin = TestGit.Origin(Path.Combine(_root, "origin", "alpha"));
        using var held = HoldLock(origin);
        var launch = Launch(new RepoRecord(origin));

        var outcome = await Provider(gitTimeout: TimeSpan.FromSeconds(1)).Start(launch).Completion.WaitAsync(Guard);

        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Failed($"Checking out {origin} failed: another session's checkout of it took more than 4 s."));
        await Assert.That(AgentStarted(launch)).IsFalse();
    }

    [Test]
    public async Task AHolderThatDies_LetsTheWaitingSessionGoOn()
    {
        var origin = TestGit.Origin(Path.Combine(_root, "origin", "alpha"));
        var held = HoldLock(origin);
        var launch = Launch(new RepoRecord(origin));
        var run = Provider().Start(launch);
        await Until(() => Log(launch).Contains("waiting for", StringComparison.Ordinal));

        // The holder dies: the kernel drops its lock, the lock file stays.
        held.Dispose();
        var outcome = await run.Completion.WaitAsync(Guard);

        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Completed(0));
        await Assert.That(File.Exists(Path.Combine(SharedRoot, RepoNames.Shared(origin) + ".lock"))).IsTrue();
    }

    [Test]
    public async Task ALockFileThatCantBeLocked_SaysSo_NotThatAnotherSessionIsSlow()
    {
        var origin = TestGit.Origin(Path.Combine(_root, "origin", "alpha"));
        Directory.CreateDirectory(Path.Combine(SharedRoot, RepoNames.Shared(origin) + ".lock"));
        var launch = Launch(new RepoRecord(origin));

        var outcome = await Provider(gitTimeout: TimeSpan.FromSeconds(1)).Start(launch).Completion.WaitAsync(Guard);

        await Assert.That(outcome).IsTypeOf<AgentOutcome.Failed>();
        var error = ((AgentOutcome.Failed)outcome).Error;
        await Assert.That(error).StartsWith($"Checking out {origin} failed: Could not open the lock file");
        await Assert.That(error).DoesNotContain("another session");
    }

    [Test]
    public async Task AHalfMadeWorktreeFromAnEarlierAttempt_IsRemoved_AndMadeWhole()
    {
        var origin = TestGit.Origin(Path.Combine(_root, "origin", "alpha"));
        var provider = Provider();
        await provider.Start(Launch(new RepoRecord(origin))).Completion.WaitAsync(Guard);
        var launch = Launch(new RepoRecord(origin));
        var shared = Shared("alpha");
        var worktree = Path.Combine(Workplace(launch), "alpha");
        // What an attempt killed in `git worktree add` leaves: the branch (with its owner), and a
        // registered worktree at the pending path with only part of the files.
        Directory.CreateDirectory(Workplace(launch));
        TestGit.Run(shared, "config", SupervisorRepo.OwnerKey(Branch(launch)), launch.SessionId.ToString());
        TestGit.Run(shared, "branch", "--no-track", Branch(launch), "refs/remotes/origin/HEAD");
        var pending = SupervisorRepo.PendingPath(worktree);
        TestGit.Run(shared, "worktree", "add", "--quiet", pending, Branch(launch));
        File.Delete(Path.Combine(pending, "README.md"));

        var outcome = await provider.Start(launch).Completion.WaitAsync(Guard);

        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Completed(0));
        await Assert.That(File.Exists(Path.Combine(worktree, "README.md"))).IsTrue();
        await Assert.That(Directory.Exists(pending)).IsFalse();
        await Assert.That((await Environment(Workplace(launch)))["PWD"]).IsEqualTo(Workplace(launch));
        await Assert.That(TestGit.Run(shared, "worktree", "list", "--porcelain")).DoesNotContain(pending);
    }

    [Test]
    public async Task SomethingElseInTheWorktreesPlace_FailsTheCheckout_AndIsLeftAlone()
    {
        var origin = TestGit.Origin(Path.Combine(_root, "origin", "alpha"));
        var launch = Launch(new RepoRecord(origin));
        var worktree = Path.Combine(Workplace(launch), "alpha");
        Directory.CreateDirectory(worktree);
        await File.WriteAllTextAsync(Path.Combine(worktree, "mine.txt"), "not a worktree");

        var outcome = await Provider().Start(launch).Completion.WaitAsync(Guard);

        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Failed($"Checking out {origin} failed: {worktree} is there already but isn't a worktree of its shared copy."));
        await Assert.That(await File.ReadAllTextAsync(Path.Combine(worktree, "mine.txt"))).IsEqualTo("not a worktree");
        await Assert.That(AgentStarted(launch)).IsFalse();
    }

    [Test]
    public async Task ABranchOfTheSameNameFromAnotherSession_IsNotTakenOver()
    {
        var origin = TestGit.Origin(Path.Combine(_root, "origin", "alpha"));
        var provider = Provider();
        var first = Launch(new RepoRecord(origin));
        await provider.Start(first).Completion.WaitAsync(Guard);
        // Another session whose id starts with the same 8 characters.
        var id = Guid.Parse(first.SessionId.ToString()[..8] + Guid.NewGuid().ToString()[8..]);
        var second = Launch(new RepoRecord(origin)) with { SessionId = id, ProviderSessionId = id };

        var outcome = await provider.Start(second).Completion.WaitAsync(Guard);

        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Failed(
            $"Checking out {origin} failed: its shared copy has a branch {Branch(first)} already, and it isn't this session's."));
        await Assert.That(AgentStarted(second)).IsFalse();
    }

    [Test]
    public async Task Ref_AnotherSessionsBranch_IsNotARefOfTheRepository()
    {
        var origin = TestGit.Origin(Path.Combine(_root, "origin", "alpha"));
        var provider = Provider();
        var first = Launch(new RepoRecord(origin));
        await provider.Start(first).Completion.WaitAsync(Guard);
        var second = Launch(new RepoRecord(origin, Branch(first)));

        var outcome = await provider.Start(second).Completion.WaitAsync(Guard);

        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Failed($"Checking out {origin} failed: '{Branch(first)}' is not a branch, tag or commit of it."));
    }

    [Test]
    public async Task Ref_AnAbbreviatedCommit_StartsFromIt()
    {
        var origin = TestGit.Origin(Path.Combine(_root, "origin", "alpha"));
        var first = TestGit.Head(origin);
        TestGit.Commit(origin, "second.txt", "second");
        var launch = Launch(new RepoRecord(origin, first[..9]));

        var outcome = await Provider().Start(launch).Completion.WaitAsync(Guard);

        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Completed(0));
        await Assert.That(TestGit.Head(Path.Combine(Workplace(launch), "alpha"))).IsEqualTo(first);
    }

    [Test]
    public async Task AStaleGitLockInTheSharedCopy_DoesntFailTheNextFetch()
    {
        var origin = TestGit.Origin(Path.Combine(_root, "origin", "alpha"));
        var provider = Provider();
        await provider.Start(Launch(new RepoRecord(origin))).Completion.WaitAsync(Guard);
        var pushed = TestGit.Commit(origin, "later.txt", "pushed later");
        // A fetch that died mid-update left its ref lock behind, a while ago.
        var stale = Path.Combine(Shared("alpha"), "refs", "remotes", "origin", "main.lock");
        await File.WriteAllTextAsync(stale, "");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-1));
        var launch = Launch(new RepoRecord(origin));

        var outcome = await provider.Start(launch).Completion.WaitAsync(Guard);

        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Completed(0));
        await Assert.That(TestGit.Head(Path.Combine(Workplace(launch), "alpha"))).IsEqualTo(pushed);
        await Assert.That(File.Exists(stale)).IsFalse();
    }

    [Test]
    public async Task HooksInTheSharedCopy_DontRun()
    {
        var origin = TestGit.Origin(Path.Combine(_root, "origin", "alpha"));
        var provider = Provider();
        await provider.Start(Launch(new RepoRecord(origin))).Completion.WaitAsync(Guard);
        // What an agent could plant in the copy every session shares.
        var ran = Path.Combine(_root, "hook-ran");
        foreach (var hook in (string[])["post-checkout", "reference-transaction"])
        {
            var path = Path.Combine(Shared("alpha"), "hooks", hook);
            await File.WriteAllTextAsync(path, $"#!/bin/sh\necho {hook} >> '{ran}'\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var outcome = await provider.Start(Launch(new RepoRecord(origin))).Completion.WaitAsync(Guard);

        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Completed(0));
        await Assert.That(File.Exists(ran)).IsFalse();
    }

    [Test]
    public async Task Delete_AWorktreeWithSubmodules_RemovesItToo()
    {
        var origin = TestGit.Origin(Path.Combine(_root, "origin", "alpha"));
        var library = TestGit.Origin(Path.Combine(_root, "origin", "library"));
        var provider = Provider();
        var launch = Launch(new RepoRecord(origin));
        await provider.Start(launch).Completion.WaitAsync(Guard);
        var worktree = Path.Combine(Workplace(launch), "alpha");
        TestGit.Run(worktree, "-c", "protocol.file.allow=always", "submodule", "--quiet", "add", library, "library");

        provider.RemoveRepos(launch.SessionId, launch.Repos!);

        await Assert.That(Directory.Exists(worktree)).IsFalse();
        await Assert.That(TestGit.Run(Shared("alpha"), "worktree", "list", "--porcelain")).DoesNotContain(worktree);
    }

    [Test]
    public async Task Delete_AWorktreeGitRefusesToRemove_DeletesItsFolder_AndPrunes()
    {
        var origin = TestGit.Origin(Path.Combine(_root, "origin", "alpha"));
        var provider = Provider();
        var launch = Launch(new RepoRecord(origin));
        await provider.Start(launch).Completion.WaitAsync(Guard);
        var worktree = Path.Combine(Workplace(launch), "alpha");
        // Its .git file broken (by the agent, say): git no longer takes it for a worktree.
        await File.WriteAllTextAsync(Path.Combine(worktree, ".git"), "gitdir: /nowhere\n");
        await Assert.That(TestGit.TryRun(Shared("alpha"), "worktree", "remove", "--force", "--force", worktree).ExitCode).IsNotEqualTo(0);

        provider.RemoveRepos(launch.SessionId, launch.Repos!);

        await Assert.That(Directory.Exists(worktree)).IsFalse();
        await Assert.That(TestGit.Run(Shared("alpha"), "worktree", "list", "--porcelain")).DoesNotContain(worktree);
    }

    [Test]
    public async Task Delete_WaitsForTheSharedCopysLock()
    {
        var origin = TestGit.Origin(Path.Combine(_root, "origin", "alpha"));
        var provider = Provider();
        var launch = Launch(new RepoRecord(origin));
        await provider.Start(launch).Completion.WaitAsync(Guard);
        var worktree = Path.Combine(Workplace(launch), "alpha");

        using (HoldLock(origin))
        {
            // Bounded: it gives up (logged) rather than meet a checkout.
            provider.RemoveRepos(launch.SessionId, launch.Repos!);
        }

        await Assert.That(Directory.Exists(worktree)).IsTrue();
        provider.RemoveRepos(launch.SessionId, launch.Repos!);
        await Assert.That(Directory.Exists(worktree)).IsFalse();
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
        await Assert.That((await Environment(Workplace(launch)))["PWD"]).IsEqualTo(Workplace(launch));
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
        await File.WriteAllTextAsync(Path.Combine(worktree, "work.txt"), "the agent's work");
        TestGit.Run(worktree, "add", "work.txt");
        TestGit.Run(worktree, "commit", "--quiet", "-m", "the agent's work");
        var committed = TestGit.Head(worktree);
        Directory.Delete(worktree, recursive: true);

        var continued = launch with { Attempt = 2, RunId = Guid.NewGuid(), Resume = run.ProviderSessionId, Messages = ["carry on"] };
        var outcome = await provider.Start(continued).Completion.WaitAsync(Guard);

        await Assert.That(outcome).IsEqualTo(new AgentOutcome.Completed(0));
        await Assert.That(TestGit.Head(worktree)).IsEqualTo(committed);
    }

    private string SharedRoot => Path.Combine(_root, "repos");

    private static string Branch(AgentLaunch launch) => RepoNames.Branch(launch.SessionId);

    private string Folder(AgentLaunch launch) => Path.Combine(_root, "sessions", launch.SessionId.ToString());

    private string Log(AgentLaunch launch) =>
        File.Exists(Path.Combine(Folder(launch), SupervisorFiles.Log)) ? File.ReadAllText(Path.Combine(Folder(launch), SupervisorFiles.Log)) : "";

    /// <summary>Holds the lock on the shared copy of <paramref name="url"/>, as another session's supervisor would.</summary>
    private Microsoft.Win32.SafeHandles.SafeFileHandle HoldLock(string url)
    {
        Directory.CreateDirectory(SharedRoot);
        var handle = Posix.OpenLockFile(Path.Combine(SharedRoot, RepoNames.Shared(url) + ".lock"));
        return Posix.TryLock(handle) ? handle : throw new InvalidOperationException("The lock is taken.");
    }

    /// <summary>Whether <paramref name="pid"/> runs in <paramref name="folder"/> (a session's supervisor does).</summary>
    private static bool IsIn(int pid, string folder)
    {
        try
        {
            return new DirectoryInfo($"/proc/{pid}/cwd").LinkTarget == folder;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(Guard);
        while (!condition())
        {
            await Task.Delay(20, timeout.Token);
        }
    }

    /// <summary>A server: its supervisors (dispose them to act out ARM dying) and a Claude provider.</summary>
    private (Supervisors Supervisors, ClaudeProvider Provider) Server()
    {
        var supervisors = new Supervisors(
            Options.Create(new SupervisorOptions { SocketRoot = Path.Combine(_root, "sockets") }), NullLogger<Supervisors>.Instance);
        var repos = new RepoCheckouts(
            Options.Create(new RepoOptions { Root = SharedRoot, GitTimeout = TimeSpan.FromSeconds(20) }), supervisors, NullLogger<RepoCheckouts>.Instance);
        var options = new ClaudeProviderOptions { Command = ClaudeProviderTests.Stub, WorkRoot = Path.Combine(_root, "sessions") };
        return (supervisors, new ClaudeProvider(Options.Create(options), supervisors, repos, NullLogger<ClaudeProvider>.Instance));
    }

    /// <summary>The arguments the stub CLI was started with, from the <c>invocation.txt</c> in <paramref name="directory"/>.</summary>
    private static async Task<string[]> Arguments(string directory) =>
        [.. (await File.ReadAllLinesAsync(Path.Combine(directory, "invocation.txt")))
            .Where(l => l.StartsWith("arg:", StringComparison.Ordinal)).Select(l => l[4..])];

    private string Shared(string name) => Directory.GetDirectories(SharedRoot, $"{name}-*.git").Single();

    private ClaudeProvider Provider(TimeSpan? gitTimeout = null, string? workRoot = null, TimeSpan? checkoutTimeout = null)
    {
        var supervisors = new Supervisors(
            Options.Create(new SupervisorOptions { SocketRoot = Path.Combine(_root, "sockets") }), NullLogger<Supervisors>.Instance);
        var repos = new RepoCheckouts(
            Options.Create(new RepoOptions { Root = SharedRoot, GitTimeout = gitTimeout ?? TimeSpan.FromSeconds(20), CheckoutTimeout = checkoutTimeout }),
            supervisors, NullLogger<RepoCheckouts>.Instance);
        var options = new ClaudeProviderOptions { Command = ClaudeProviderTests.Stub, WorkRoot = workRoot ?? Path.Combine(_root, "sessions") };
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
