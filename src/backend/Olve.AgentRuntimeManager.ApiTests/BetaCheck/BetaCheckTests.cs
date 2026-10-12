using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Olve.AgentRuntimeManager.ApiTests.BetaCheck;

/// <summary>
/// The beta check (<c>src/deploy/vm/beta-check.py</c>, run by the pipeline's deploy-beta in the VM)
/// against a local ARM with a stub Claude Code (<see cref="LocalArm"/>): on a working ARM every
/// check passes, once per version; each failure the stub can bring about fails the check and says
/// what failed, and leaves nothing running. What a working ARM can't be made to do (a wrong
/// conversation, activity or event) is tested on the check's own logic (<c>beta_check_test.py</c>).
/// Starts ARM itself, so not against a base-URL target. Needs <c>python3</c> (mise.toml pins it).
/// </summary>
[UnsupportedOSPlatform("windows")]
public class BetaCheckTests
{
    private static readonly string[] Checks =
    [
        "health", "server info", "auth", "earlier runs", "provider health", "event stream", "tool session",
        "conversation", "activity", "continue", "kill", "delete", "lifecycle events", "record",
    ];

    [Before(Test)]
    public void LocalOnly() =>
        Skip.When(Environment.GetEnvironmentVariable(ApiTarget.BaseUrlVariable) is { Length: > 0 },
            $"Starts an ARM of its own; not with {ApiTarget.BaseUrlVariable} set.");

    [Test]
    public async Task Check_OnAWorkingArm_PassesEveryCheck_OncePerVersion()
    {
        await using var arm = await LocalArm.StartAsync();
        // As an earlier run that failed left it: still working.
        var leftover = await CreateCheckSessionAsync(arm, "```sh\nsleep 60\n```");

        var first = await arm.RunCheckAsync();

        await Assert.That(first.ExitCode).IsEqualTo(0).Because(first.ToString());
        foreach (var check in Checks)
        {
            await Assert.That(first.Output).Contains($"ok   {check}:");
        }

        await Assert.That(first.Output).Contains($"killed session {leftover}");
        await Assert.That(first.Output).Contains("(deleting it was refused while it worked, 409) deleted once it completed, then 404");
        await Assert.That(first.Output).Contains($"Beta check passed for {LocalArm.Version} (3 model turns)");
        var sessions = await arm.CheckSessionsAsync();
        // The leftover (killed by the check), the continued one, the killed one and the record; the deleted one is gone.
        await Assert.That(sessions.Select(s => s.Status).Order()).IsEquivalentTo(["completed", "completed", "killed", "killed"]);
        await Assert.That(await arm.EnvAsync()).IsEmpty();

        // A retried deploy of the same version starts nothing.
        var again = await arm.RunCheckAsync();

        await Assert.That(again.ExitCode).IsEqualTo(0).Because(again.ToString());
        await Assert.That(again.Output).Contains($"{LocalArm.Version} already passed (record ");
        await Assert.That(again.Output).DoesNotContain("ok   tool session");
        await Assert.That(again.Output).Contains("(0 model turns)");
        await Assert.That(await arm.CheckSessionsAsync()).IsEquivalentTo(sessions);
    }

    [Test]
    [Arguments("no-env", "", "FAIL tool session: the registered default variable BETA_CHECK_NONCE didn't reach the agent (it saw '')")]
    [Arguments("leak-env", "", "FAIL tool session: ARM's own configuration (Auth__Authority) reached the agent's environment")]
    [Arguments("no-tool", "", "FAIL tool session: the agent made no Bash tool call (tool calls: none)")]
    [Arguments("ok", "BETA_CHECK_AGENT_USER=nobody", "', expected 'nobody'")]
    [Arguments("ok", "BETA_CHECK_SAME_USER=0", "the user ARM (and this check) runs as")]
    [Arguments("leak", "BETA_CHECK_KILL_SECONDS=2", "FAIL kill: 2 processes of killed session")]
    [Arguments("orphan", "BETA_CHECK_KILL_SECONDS=2", "FAIL kill: 1 processes of killed session")]
    [Arguments("idle-call", "", "called Bash, but nothing ran under it within 10s: no command to kill")]
    [Arguments("logged-out", "", "FAIL provider health: the claude provider is unauthorized")]
    [Arguments("ok", "BETA_CHECK_VERSION=2026.1.1.1", "FAIL server info: ARM says it is version '2026.10.12.1' in 'beta', expected '2026.1.1.1' in 'beta'")]
    public async Task Check_WhenSomethingIsWrong_FailsSayingWhat_AndLeavesNothingRunning(string mode, string setting, string expected)
    {
        await using var arm = await LocalArm.StartAsync(mode);

        var run = await arm.RunCheckAsync(setting.Length > 0 ? [setting] : []);

        await Assert.That(run.ExitCode).IsEqualTo(1).Because(run.ToString());
        await Assert.That(run.Output).Contains(expected);
        await Assert.That(run.Output).Contains("Beta check FAILED");
        await Assert.That(run.Output).DoesNotContain("Traceback");
        await Assert.That((await arm.CheckSessionsAsync()).Where(s => s.Status is "queued" or "working")).IsEmpty();
        await Assert.That(await arm.EnvAsync()).IsEmpty();
    }

    [Test]
    public async Task Check_WithoutAFakeProvider_DeletesTheKilledSession_AndKeepsNoRecord()
    {
        await using var arm = await LocalArm.StartAsync("ok", "Providers__Fake__Enabled=false");

        var run = await arm.RunCheckAsync();

        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.ToString());
        await Assert.That(run.Output).Contains("ok   delete: the killed session ");
        await Assert.That(run.Output).Contains("ok   record: no fake provider, so no record");
        await Assert.That((await arm.CheckSessionsAsync()).Select(s => s.Status)).IsEquivalentTo(["completed"]);
    }

    [Test]
    public async Task Check_WhenAnAgentIsStillWorking_KillsItBeforeItEnds()
    {
        await using var arm = await LocalArm.StartAsync("hang");

        var run = await arm.RunCheckAsync("BETA_CHECK_TURN_SECONDS=3");

        await Assert.That(run.ExitCode).IsEqualTo(1).Because(run.ToString());
        await Assert.That(run.Output).Contains("cleanup: killed session");
        await Assert.That((await arm.CheckSessionsAsync()).Select(s => s.Status)).IsEquivalentTo(["killed"]);
    }

    [Test]
    public async Task DeployBeta_SendsTheCheckWithItsSecretOnStdin_NeverAsAnArgument()
    {
        const string Secret = "s3cr+t/with \"quotes\", 'apostrophes' and $dollars";

        var sent = await DeployBetaAsync(Secret);

        await Assert.That(sent.ExitCode).IsEqualTo(0).Because(sent.Output);
        await Assert.That(sent.Arguments).Contains("timeout -k 15 210 python3 -u -");
        await Assert.That(sent.Arguments + sent.Output).DoesNotContain("s3cr");
        var preset = sent.Stdin[..sent.Stdin.IndexOf('\n', StringComparison.Ordinal)];
        await Assert.That(preset).StartsWith("PRESET = ");
        using var settings = JsonDocument.Parse(preset["PRESET = ".Length..]);
        await Assert.That(settings.RootElement.GetProperty("BETA_CHECK_VERSION").GetString()).IsEqualTo("2026.10.12.4");
        await Assert.That(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(
            settings.RootElement.GetProperty("BETA_CHECK_CLIENT_SECRET_B64").GetString()!))).IsEqualTo(Secret);
        await Assert.That(sent.Stdin[(preset.Length + 1)..])
            .IsEqualTo(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "BetaCheck", "beta-check.py")));
    }

    [Test]
    public async Task DeployBeta_ABundleWithoutItsCheck_FailsSayingSo()
    {
        var sent = await DeployBetaAsync("secret", withCheck: false);

        await Assert.That(sent.ExitCode).IsNotEqualTo(0);
        await Assert.That(sent.Output).Contains("no beta check (vm/beta-check.py): not confirming beta");
        await Assert.That(sent.Stdin).IsEmpty();
    }

    [Test]
    public async Task DeployBeta_WithoutTheSecret_FailsSayingSo()
    {
        var sent = await DeployBetaAsync(secret: "");

        await Assert.That(sent.ExitCode).IsNotEqualTo(0);
        await Assert.That(sent.Output).Contains("ARM_BETA_OIDC_CLIENT_SECRET is not set");
        await Assert.That(sent.Stdin).IsEmpty();
    }

    /// <summary>deploy-beta.sh's part that runs the check, with `ssh` recording its arguments and stdin.</summary>
    private static async Task<(int ExitCode, string Output, string Arguments, string Stdin)> DeployBetaAsync(string secret, bool withCheck = true)
    {
        var root = Directory.CreateTempSubdirectory("arm-deploy-beta-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "vm"));
            if (withCheck)
            {
                File.Copy(Path.Combine(AppContext.BaseDirectory, "BetaCheck", "beta-check.py"), Path.Combine(root, "vm", "beta-check.py"));
            }

            var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "BetaCheck", "deploy-beta.sh"));
            var part = script[script.IndexOf("# ARM's beta checks", StringComparison.Ordinal)..];
            await File.WriteAllTextAsync(Path.Combine(root, "run.sh"), $$"""
                set -e
                : > "$ROOT/args"; : > "$ROOT/stdin"
                ssh() { printf '%s\n' "$@" > "$ROOT/args"; cat > "$ROOT/stdin"; }
                INPUT_DIR=$ROOT CLAUDE_FILE=$ROOT/claude-version.txt VERSION=2026.10.12.4 HOST=bulwark
                {{part}}
                """);
            var info = new ProcessStartInfo("sh", [Path.Combine(root, "run.sh")]) { RedirectStandardOutput = true, RedirectStandardError = true };
            info.Environment["ROOT"] = root;
            info.Environment["ARM_BETA_OIDC_CLIENT_SECRET"] = secret;
            using var process = Process.Start(info)!;
            var output = await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            return (process.ExitCode, output,
                await File.ReadAllTextAsync(Path.Combine(root, "args")), await File.ReadAllTextAsync(Path.Combine(root, "stdin")));
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

    /// <summary>A session as the check creates them (its caller, its tag).</summary>
    private static async Task<string> CreateCheckSessionAsync(LocalArm arm, string prompt)
    {
        using var client = arm.Client();
        using var response = await client.PostAsJsonAsync("/api/sessions", new
        {
            prompt, provider = "claude", model = "haiku", caller = "deploy-beta",
            env = new Dictionary<string, string> { ["BETA_CHECK_VERSION"] = "2026.1.1.0" },
        });
        response.EnsureSuccessStatusCode();
        using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return created.RootElement.GetProperty("id").GetString()!;
    }
}
