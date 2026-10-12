using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Olve.AgentRuntimeManager.ApiTests.BetaCheck;

/// <summary>
/// The beta check (<c>.pipelines/scripts/beta-check.py</c>, run by the pipeline's test-after-beta
/// from its own job, over HTTP only) against a local ARM with a stub Claude Code
/// (<see cref="LocalArm"/>): on a working ARM every check passes, once per version, with one model
/// turn; each failure the stub can bring about fails the check and says what failed, and leaves
/// nothing running. What a working ARM can't be made to do (a wrong conversation, activity or event)
/// is tested on the check's own logic (<c>beta_check_test.py</c>). Also test-after-beta's part that
/// runs it. Starts ARM itself, so not against a base-URL target. Needs <c>python3</c> (mise.toml pins it).
/// </summary>
[UnsupportedOSPlatform("windows")]
public class BetaCheckTests
{
    private static readonly string[] Checks =
    [
        "health", "server info", "auth", "earlier runs", "provider health", "event stream", "conversation",
        "activity", "continue", "kill and delete", "claude session", "lifecycle events", "record",
    ];

    [Before(Test)]
    public void LocalOnly() =>
        Skip.When(Environment.GetEnvironmentVariable(ApiTarget.BaseUrlVariable) is { Length: > 0 },
            $"Starts an ARM of its own; not with {ApiTarget.BaseUrlVariable} set.");

    [Test]
    public async Task Check_OnAWorkingArm_PassesEveryCheck_OncePerVersion_WithOneModelTurn()
    {
        await using var arm = await LocalArm.StartAsync();
        // As an earlier run that failed left it: still working.
        var leftover = await CreateCheckSessionAsync(arm, "fake:hang");
        // As an earlier run that couldn't delete its claude session left it: ended, its worktree still there.
        var withRepo = await CreateCheckSessionAsync(arm, "Reply with one word.", "claude", arm.Repo);
        await WaitEndedAsync(arm, withRepo);
        await Assert.That(arm.Worktrees()).Count().IsEqualTo(1);

        var first = await arm.RunCheckAsync();

        await Assert.That(first.ExitCode).IsEqualTo(0).Because(first.ToString());
        foreach (var check in Checks)
        {
            await Assert.That(first.Output).Contains($"ok   {check}:");
        }

        await Assert.That(first.Output).Contains($"killed session {leftover}");
        await Assert.That(first.Output).Contains($"deleted session {withRepo} (and its worktree)");
        await Assert.That(first.Output).Contains("deleting it while it worked was refused (409), the kill ended it killed");
        await Assert.That(first.Output).Contains($"Beta check passed for {LocalArm.Version} (1 model turn)");
        await Assert.That(first.Output).Contains("beta-check-repo (the API's folder) is a work tree of its own on arm/");
        var sessions = await arm.CheckSessionsAsync();
        // The leftover (killed by the check, kept: it has no repository), the continued fake one and the record; the killed fake one
        // and the claude ones (the earlier run's and this one's) are deleted.
        await Assert.That(sessions.Select(s => s.Status).Order()).IsEquivalentTo(["completed", "completed", "killed"]);
        await Assert.That(sessions.Count(s => s.Provider == "claude")).IsEqualTo(0);
        // Deleting the claude session removed its worktree: the shared copy names none but itself.
        await Assert.That(arm.Worktrees()).IsEmpty();
        await Assert.That(await arm.EnvAsync()).IsEmpty();

        // A retried run for the same version starts nothing.
        var again = await arm.RunCheckAsync();

        await Assert.That(again.ExitCode).IsEqualTo(0).Because(again.ToString());
        await Assert.That(again.Output).Contains($"{LocalArm.Version} already passed (record ");
        await Assert.That(again.Output).DoesNotContain("ok   claude session");
        await Assert.That(again.Output).Contains("(0 model turns)");
        await Assert.That(await arm.CheckSessionsAsync()).IsEquivalentTo(sessions);
    }

    [Test]
    [Arguments("no-env", "", "FAIL claude session: the registered default variable BETA_CHECK_NONCE didn't reach the agent (it saw '')")]
    [Arguments("leak-env", "", "FAIL claude session: ARM's own configuration (Auth__Authority) reached the agent's environment")]
    [Arguments("no-tool", "", "FAIL claude session: the agent made no Bash tool call (tool calls: none)")]
    [Arguments("ok", "BETA_CHECK_AGENT_USER=nobody", "', expected 'nobody'")]
    [Arguments("ok", "BETA_CHECK_AGENT_USER=beta-check-agent BETA_CHECK_NOT_AGENT_USERS=arm,{user}", "', a user agents must never run as")]
    [Arguments("logged-out", "", "FAIL provider health: the claude provider (the model turn's) is unauthorized")]
    [Arguments("ok", "BETA_CHECK_VERSION=2026.1.1.1", "FAIL server info: ARM says it is version '2026.10.12.1' in 'beta', expected '2026.1.1.1' in 'beta'")]
    [Arguments("ok", "BETA_CHECK_TOKEN=not-a-token", "FAIL server info: GET /api/server-info answered 401")]
    [Arguments("wrong-branch", "", "FAIL claude session: session {id}'s checkout of {repo}: beta-check-repo is on the branch 'beta-check-other', not the session's 'arm/{id8}'")]
    [Arguments("no-checkout", "", "FAIL claude session: session {id}'s checkout of {repo}: its workplace has no folder 'beta-check-repo' (the command saw ['beta-check-repo-moved'])")]
    [Arguments("nested", "", "beta-check-repo isn't a checkout of its own: it's inside the work tree ")]
    public async Task Check_WhenSomethingIsWrong_FailsSayingWhat_AndLeavesNothingRunning(string mode, string setting, string expected)
    {
        await using var arm = await LocalArm.StartAsync(mode);

        var run = await arm.RunCheckAsync(setting.Replace("{user}", LocalArm.User, StringComparison.Ordinal).Split(' ', StringSplitOptions.RemoveEmptyEntries));

        await Assert.That(run.ExitCode).IsEqualTo(1).Because(run.ToString());
        var claude = ClaudeSessionId(run.Output);
        await Assert.That(run.Output).Contains(expected
            .Replace("{repo}", arm.Repo, StringComparison.Ordinal)
            .Replace("{id8}", claude?[..Math.Min(8, claude.Length)] ?? "?", StringComparison.Ordinal)
            .Replace("{id}", claude ?? "?", StringComparison.Ordinal));
        await Assert.That(run.Output).Contains("Beta check FAILED");
        await Assert.That(run.Output).DoesNotContain("Traceback");
        var left = await arm.CheckSessionsAsync();
        await Assert.That(left.Where(s => s.Status is "queued" or "working")).IsEmpty();
        // A claude session (it has the repository) is deleted, its worktree with it.
        await Assert.That(left.Where(s => s.Provider == "claude")).IsEmpty();
        await Assert.That(arm.Worktrees()).IsEmpty();
        await Assert.That(await arm.EnvAsync()).IsEmpty();
    }

    [Test]
    public async Task Check_WithoutAFakeProvider_FailsBeforeAnySession()
    {
        await using var arm = await LocalArm.StartAsync("ok", "Providers__Fake__Enabled=false");

        var run = await arm.RunCheckAsync();

        await Assert.That(run.ExitCode).IsEqualTo(1).Because(run.ToString());
        await Assert.That(run.Output).Contains("FAIL provider health: no fake provider (every other check's (it's enabled on beta))");
        await Assert.That(await arm.CheckSessionsAsync()).IsEmpty();
    }

    [Test]
    public async Task Check_WhenTheAgentIsStillWorking_KillsItBeforeItEnds()
    {
        await using var arm = await LocalArm.StartAsync("hang");

        var run = await arm.RunCheckAsync("BETA_CHECK_TURN_SECONDS=3", "BETA_CHECK_CHECKOUT_SECONDS=2");

        await Assert.That(run.ExitCode).IsEqualTo(1).Because(run.ToString());
        await Assert.That(run.Output).Contains("FAIL claude session: session ");
        await Assert.That(run.Output).Contains("is still working after 5s");
        await Assert.That(run.Output).Contains("cleanup: killed session");
        await Assert.That(run.Output).Contains("cleanup: deleted session");
        await Assert.That((await arm.CheckSessionsAsync()).Where(s => s.Provider == "claude")).IsEmpty();
        await Assert.That(arm.Worktrees()).IsEmpty();
    }

    [Test]
    public async Task Check_WithoutAWayToAToken_FailsSayingWhatIsMissing()
    {
        await using var arm = await LocalArm.StartAsync();

        var run = await arm.RunCheckAsync("BETA_CHECK_TOKEN=", "BETA_CHECK_AUTHORITY=https://auth.example/", "BETA_CHECK_CLIENT_ID=olve-arm");

        await Assert.That(run.ExitCode).IsEqualTo(1).Because(run.ToString());
        await Assert.That(run.Output).Contains("FAIL setup: no BETA_CHECK_TOKEN, and BETA_CHECK_CLIENT_SECRET not set: no way to get a token");
        await Assert.That(await arm.CheckSessionsAsync()).IsEmpty();
    }

    [Test]
    public async Task TestAfterBeta_RunsTheCheckWithItsSecretInTheEnvironment_NeverAsAnArgument()
    {
        const string Secret = "s3cr+t/with \"quotes\", 'apostrophes' and $dollars";

        var step = await TestAfterBetaAsync(Secret);

        await Assert.That(step.ExitCode).IsEqualTo(0).Because(step.Output);
        await Assert.That(step.Ssh).Contains("kubectl --request-timeout=20s -n apps-beta get configmap authentik-ca -o jsonpath='{.data.auth-beta-ca\\.crt}'");
        await Assert.That(step.Check).Contains("-k 30 300 /mise/python3 -u .pipelines/scripts/beta-check.py");
        await Assert.That(step.Check).Contains("BETA_CHECK_API=http://100.100.117.17:18792\n");
        await Assert.That(step.Check).Contains("BETA_CHECK_VERSION=2026.10.12.4\n");
        await Assert.That(step.Check).Contains("BETA_CHECK_AUTHORITY=https://auth-beta.ovea.pro/application/o/olve-arm/\n");
        await Assert.That(step.Check).Contains("BETA_CHECK_CLIENT_ID=olve-arm\n");
        await Assert.That(step.Check).Contains($"BETA_CHECK_CLIENT_SECRET={Secret}\n");
        // The CA it read (here in the test's folder, not /tmp).
        await Assert.That(step.Check).Contains("BETA_CHECK_CA_FILE=/").And.Contains("/ca.crt\n");
        await Assert.That(step.Check).Contains("BETA_CHECK_REPO_URL=git@github.com:OliverVea/Olve.AgentRuntimeManager.git\n");
        // The first line is how the check is started: its arguments.
        await Assert.That(step.Ssh + step.Check.Split('\n')[0] + step.Output).DoesNotContain("s3cr");
        await Assert.That(step.Output).Contains("Beta confirmed");
    }

    [Test]
    [Arguments("secret", "key", true, true, true, true, "Beta confirmed")]
    [Arguments("", "key", true, true, true, false, "ARM_BETA_OIDC_CLIENT_SECRET is not set: can't get a token to check beta")]
    [Arguments("secret", "", true, true, true, false, "SSH_PRIVATE_KEY is not set: can't read beta's Authentik CA through oliver@bulwark-m2")]
    [Arguments("secret", "key", false, true, true, false, "Can't read beta's Authentik CA (configMap apps-beta/authentik-ca, key auth-beta-ca.crt) through oliver@bulwark-m2")]
    [Arguments("secret", "key", true, false, true, false, "This bundle (2026.10.12.4) has no Claude Code (it predates the claude-code step): no beta checks")]
    [Arguments("secret", "key", true, true, false, false, "This bundle's commit (0123abcdef) predates the post-deploy beta checks: none to run")]
    public async Task TestAfterBeta_SaysWhyItDoesNotCheck(string secret, string key, bool caReadable, bool withClaude, bool withChecks, bool checks, string expected)
    {
        var step = await TestAfterBetaAsync(secret, caReadable, withClaude, withChecks: withChecks, key: key);

        await Assert.That(step.Output).Contains(expected);
        await Assert.That(step.Check.Length > 0).IsEqualTo(checks);
        await Assert.That(step.ExitCode == 0).IsEqualTo(checks || !withClaude || !withChecks).Because(step.Output);
    }

    [Test]
    public async Task TestAfterBeta_TestsWithTheBundlesCommit_OrSaysItUsesMain()
    {
        var withCommit = await TestAfterBetaAsync("secret");
        var without = await TestAfterBetaAsync("secret", commit: null);

        await Assert.That(withCommit.Ref).IsEqualTo("0123abcdef\n");
        await Assert.That(withCommit.Output).DoesNotContain("names no commit");
        await Assert.That(without.ExitCode).IsEqualTo(0).Because(without.Output);
        await Assert.That(without.Ref).IsEqualTo("main\n");
        await Assert.That(without.Output).Contains("The bundle names no commit (commit.txt): testing with main's head");
    }

    [Test]
    public async Task TestAfterBeta_WhenBetaNeverGetsHealthy_FailsBeforeTesting()
    {
        var step = await TestAfterBetaAsync("secret", healthy: false);

        await Assert.That(step.ExitCode).IsNotEqualTo(0);
        await Assert.That(step.Output).Contains("beta isn't healthy at http://100.100.117.17:18792/health after 30 tries");
        await Assert.That(step.Output).DoesNotContain("the API suite ran");
        await Assert.That(step.Check).IsEmpty();
    }

    [Test]
    public async Task TestAfterBeta_AFailedCheck_FailsTheStep()
    {
        var step = await TestAfterBetaAsync("secret", checkExitCode: 1);

        await Assert.That(step.ExitCode).IsNotEqualTo(0);
        await Assert.That(step.Output).DoesNotContain("Beta confirmed");
    }

    /// <summary>
    /// test-after-beta.sh from the bundle on (its commit, beta's health, the API suite, the checks),
    /// with the fetch recording the ref, `curl` answering health as told, `mise` standing in for the
    /// suite, `ssh` recording its arguments and answering a CA (or nothing), and `timeout` recording
    /// how the check is started and the settings in its environment.
    /// </summary>
    private static async Task<(int ExitCode, string Output, string Ssh, string Check, string Ref)> TestAfterBetaAsync(
        string secret, bool caReadable = true, bool withClaude = true, int checkExitCode = 0, bool withChecks = true, string key = "a key",
        string? commit = "OliverVea/Olve.AgentRuntimeManager@0123abcdef", bool healthy = true)
    {
        var root = Directory.CreateTempSubdirectory("arm-test-after-beta-").FullName;
        try
        {
            var bundle = Path.Combine(root, "input", "build-and-package");
            Directory.CreateDirectory(bundle);
            await File.WriteAllTextAsync(Path.Combine(bundle, "version.txt"), "2026.10.12.4\n");
            if (commit is not null)
            {
                await File.WriteAllTextAsync(Path.Combine(bundle, "commit.txt"), commit + "\n");
            }
            if (withClaude)
            {
                Directory.CreateDirectory(Path.Combine(root, "input", "claude-code"));
                await File.WriteAllTextAsync(Path.Combine(root, "input", "claude-code", "claude-version.txt"), "2.1.0\n");
            }

            // The bundle's commit, checked out.
            Directory.CreateDirectory(Path.Combine(root, "src", ".pipelines", "scripts"));
            if (withChecks)
            {
                await File.WriteAllTextAsync(Path.Combine(root, "src", ".pipelines", "scripts", "beta-check.py"), "");
            }

            var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "BetaCheck", "test-after-beta.sh"));
            var part = script[script.IndexOf("INPUT_DIR=$(olve_bundle_input)", StringComparison.Ordinal)..]
                .Replace("/input/", $"{root}/input/", StringComparison.Ordinal)
                .Replace("/tmp/authentik-beta-ca.crt", $"{root}/ca.crt", StringComparison.Ordinal)
                .Replace("\"$REF\" /src", "\"$REF\" \"$ROOT/src\"", StringComparison.Ordinal)
                .Replace("cd /src", "cd \"$ROOT/src\"", StringComparison.Ordinal);
            await File.WriteAllTextAsync(Path.Combine(root, "run.sh"), $$"""
                set -e
                : > "$ROOT/ssh"; : > "$ROOT/check"; : > "$ROOT/ref"
                olve_bundle_input() { echo "$ROOT/input/build-and-package"; }
                olve_fetch_repo() { echo "$2" > "$ROOT/ref"; }
                curl() { case "$*" in *mise.run*) return 0 ;; esac; [ "$HEALTHY" = 1 ]; }
                sleep() { :; }
                ssh() { printf '%s\n' "$@" >> "$ROOT/ssh"; [ "$CA_READABLE" = 1 ] && printf -- '-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----\n'; return 0; }
                mise() { case "$1" in which) echo /mise/python3 ;; run) echo "the API suite ran" ;; esac; }
                timeout() {
                  case " $* " in
                    *" ssh "*) while [ "$1" != ssh ]; do shift; done; "$@"; return ;;
                  esac
                  printf '%s ' "$@" > "$ROOT/check"; echo >> "$ROOT/check"
                  env | grep '^BETA_CHECK_' >> "$ROOT/check"
                  return "$CHECK_EXIT"
                }
                HOME=$ROOT REPO=OliverVea/Olve.AgentRuntimeManager
                {{part}}
                """);
            var info = new ProcessStartInfo("sh", [Path.Combine(root, "run.sh")]) { RedirectStandardOutput = true, RedirectStandardError = true };
            info.Environment["ROOT"] = root;
            info.Environment["ARM_BETA_OIDC_CLIENT_SECRET"] = secret;
            info.Environment["SSH_PRIVATE_KEY"] = key;
            info.Environment["CA_READABLE"] = caReadable ? "1" : "0";
            info.Environment["CHECK_EXIT"] = checkExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
            info.Environment["HEALTHY"] = healthy ? "1" : "0";
            using var process = Process.Start(info)!;
            var output = await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            return (process.ExitCode, output, await File.ReadAllTextAsync(Path.Combine(root, "ssh")),
                await File.ReadAllTextAsync(Path.Combine(root, "check")), await File.ReadAllTextAsync(Path.Combine(root, "ref")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task CheckLogic_PassesItsOwnTests()
    {
        var info = new ProcessStartInfo("python3")
        {
            WorkingDirectory = Path.Combine(AppContext.BaseDirectory, "BetaCheck"),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add("beta_check_test.py");
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(process.ExitCode).IsEqualTo(0).Because(await output + await errors);
    }

    /// <summary>The claude session's id, from the line that names it ("session &lt;id&gt; …" in its check's line).</summary>
    private static string? ClaudeSessionId(string output) =>
        System.Text.RegularExpressions.Regex.Match(output, @"(?:ok   |FAIL )claude session: session ([0-9a-f-]{36})") is { Success: true } match
            ? match.Groups[1].Value
            : null;

    /// <summary>A session as the check creates them (its caller, its tag).</summary>
    private static async Task<string> CreateCheckSessionAsync(LocalArm arm, string prompt, string provider = "fake", string? repo = null)
    {
        using var client = arm.Client();
        using var response = await client.PostAsJsonAsync("/api/sessions", new
        {
            prompt, provider, model = provider == "fake" ? "fake" : "haiku", caller = "test-after-beta",
            env = new Dictionary<string, string> { ["BETA_CHECK_VERSION"] = "2026.1.1.0" },
            repos = repo is null ? null : new[] { new { url = repo } },
        });
        response.EnsureSuccessStatusCode();
        using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return created.RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>Waits (bounded) until the session has ended.</summary>
    private static async Task WaitEndedAsync(LocalArm arm, string id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while ((await arm.CheckSessionsAsync()).Single(s => s.Id == id).Status is "queued" or "working")
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Session {id} didn't end within 30 s.");
            }

            await Task.Delay(200);
        }
    }
}
