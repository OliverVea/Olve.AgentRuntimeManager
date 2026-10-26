using System.Diagnostics;
using System.Runtime.Versioning;
using Olve.AgentRuntimeManager.Tools.Tools;

namespace Olve.AgentRuntimeManager.UnitTests.Tools;

/// <summary><c>arm_bash</c>: a fresh shell per call, its output and exit code, and nothing it starts left running.</summary>
[UnsupportedOSPlatform("windows")]
public class BashToolTests
{
    private readonly ToolsTestFolder _folder = new();

    [After(Test)]
    public void Cleanup() => _folder.Dispose();

    private Task<Olve.AgentRuntimeManager.Tools.Calls.ToolResult> Run(string json, CancellationToken cancellationToken = default) =>
        BashTool.RunAsync(ToolsTestFolder.Arguments(json), _folder.Context, cancellationToken);

    [Test]
    public async Task Run_ReturnsStdoutAndStderrTogether_AndTheExitCode()
    {
        var result = await Run("""{ "command": "echo out; echo err >&2; exit 3" }""");

        await Assert.That(result.IsError).IsFalse();
        await Assert.That(result.Text).IsEqualTo("out\nerr\n\nExit code: 3");
    }

    [Test]
    public async Task Run_RunsInTheWorkplace_OrARelativeWorkingDir()
    {
        Directory.CreateDirectory(_folder.PathOf("sub"));

        var inWorkplace = await Run("""{ "command": "pwd" }""");
        var inSub = await Run("""{ "command": "pwd", "working_dir": "sub" }""");

        await Assert.That(inWorkplace.Text).StartsWith(_folder.Workplace + "\n");
        await Assert.That(inSub.Text).StartsWith(_folder.PathOf("sub") + "\n");
    }

    [Test]
    public async Task Run_IsStateless_NothingCarriesOver()
    {
        Directory.CreateDirectory(_folder.PathOf("sub"));
        await Run("""{ "command": "cd sub; export ARM_TEST_VAR=1" }""");

        var result = await Run("""{ "command": "pwd; echo \"[$ARM_TEST_VAR]\"" }""");

        await Assert.That(result.Text).StartsWith($"{_folder.Workplace}\n[]\n");
    }

    [Test]
    public async Task Run_StdinIsEmpty()
    {
        var watch = Stopwatch.StartNew();
        var result = await Run("""{ "command": "cat; read line; echo \"read: $?\"" }""");

        await Assert.That(result.Text).StartsWith("read: 1");
        await Assert.That(watch.Elapsed).IsLessThan(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task Run_AMissingWorkingDir_IsAnError()
    {
        var result = await Run("""{ "command": "true", "working_dir": "nowhere" }""");

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Text).Contains("doesn't exist");
    }

    [Test]
    [Arguments(601)]
    [Arguments(0)]
    public async Task Run_ATimeoutOutOfRange_IsAnError(int seconds)
    {
        var result = await Run($$"""{ "command": "true", "timeout_seconds": {{seconds}} }""");

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Text).Contains("`timeout_seconds` must be from 1 to 600");
    }

    [Test]
    public async Task Run_NoCommand_IsAnError()
    {
        var result = await Run("""{ "description": "nothing" }""");

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Text).IsEqualTo("`command` is required.");
    }

    [Test]
    public async Task Run_AtItsTimeout_StopsTheWholeGroup()
    {
        var marker = ToolsTestFolder.Marker();
        var watch = Stopwatch.StartNew();

        var result = await Run($$"""{ "command": "echo started; (exec -a {{marker}} sleep 300) & (exec -a {{marker}} sleep 300) & wait", "timeout_seconds": 1 }""");

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Text).Contains("started");
        await Assert.That(result.Text).Contains("Timed out after 1 s");
        await Assert.That(watch.Elapsed).IsLessThan(TimeSpan.FromSeconds(10));
        await Assert.That(await ToolsTestFolder.Gone(marker)).IsTrue();
    }

    [Test]
    public async Task Run_WhatTheCommandLeftRunning_IsStopped()
    {
        var marker = ToolsTestFolder.Marker();

        var result = await Run($$"""{ "command": "(exec -a {{marker}} sleep 300) & echo left" }""");

        await Assert.That(result.Text).IsEqualTo("left\n\nExit code: 0");
        await Assert.That(await ToolsTestFolder.Gone(marker)).IsTrue();
    }

    [Test]
    public async Task Run_Cancelled_StopsTheGroup()
    {
        var marker = ToolsTestFolder.Marker();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var result = await Run($$"""{ "command": "exec -a {{marker}} sleep 300" }""", cancel.Token);

        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Text).Contains("Cancelled");
        await Assert.That(await ToolsTestFolder.Gone(marker)).IsTrue();
    }

    [Test]
    public async Task Run_LongOutput_KeepsTheHeadAndTheTail()
    {
        var result = await Run("""{ "command": "echo FIRST; yes line | head -n 100000; echo LAST" }""");

        await Assert.That(result.Text).StartsWith("FIRST\n");
        await Assert.That(result.Text).EndsWith("LAST\n\nExit code: 0");
        await Assert.That(result.Text).Contains("characters of output cut here");
        await Assert.That(result.Text.Length).IsLessThan(CappedOutput.DefaultHead + CappedOutput.DefaultTail + 500);
    }
}
