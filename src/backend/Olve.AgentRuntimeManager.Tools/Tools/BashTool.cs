using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using Olve.AgentRuntimeManager.Tools.Calls;

namespace Olve.AgentRuntimeManager.Tools.Tools;

/// <summary>
/// <c>arm_bash</c>: one command in a fresh <c>bash -c</c>, nothing carried over between calls. It
/// runs in a session of its own (so its group can be stopped whole), with stdin from /dev/null
/// and stdout and stderr captured together (never the server's own stdio, which is the MCP
/// channel), capped (<see cref="CappedOutput"/>). Whatever is left of the group when the command
/// ends, on its timeout or when the call is cancelled is stopped: nothing a call starts outlives
/// it. Background jobs that should are for later, as a tool of their own or an argument.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal static class BashTool
{
    public const int DefaultTimeoutSeconds = 120;
    public const int MaxTimeoutSeconds = 600;

    /// <summary>
    /// The command as bash's <c>$1</c>, run by a bash that first points stdin at /dev/null and
    /// stderr at stdout (one stream, in the order written), then execs the one that runs it: the
    /// same process throughout, so <c>setsid</c>'s pid is the group's id.
    /// </summary>
    private const string Wrapper = "exec bash -c \"$1\" </dev/null 2>&1";

    /// <summary>How long the output may still arrive after bash has exited (it's read on its own).</summary>
    private static readonly TimeSpan Drain = TimeSpan.FromMilliseconds(500);

    public static async Task<ToolResult> RunAsync(ToolArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var command = arguments.RequiredString("command");
        _ = arguments.OptionalString("description");
        var workingDir = arguments.OptionalString("working_dir");
        var timeoutSeconds = arguments.OptionalInteger("timeout_seconds") ?? DefaultTimeoutSeconds;
        if (timeoutSeconds is < 1 or > MaxTimeoutSeconds)
        {
            arguments.Problems.Add($"`timeout_seconds` must be from 1 to {MaxTimeoutSeconds} (it was {timeoutSeconds}).");
        }

        if (arguments.Failure is { } failure)
        {
            return failure;
        }

        var directory = workingDir is null ? context.Workplace : ToolPaths.Resolve(context.Workplace, workingDir);
        if (!Directory.Exists(directory))
        {
            return ToolResult.Error($"The working directory {directory} doesn't exist (or isn't a folder).");
        }

        var info = new ProcessStartInfo("setsid")
        {
            WorkingDirectory = directory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in (string[])["bash", "-c", Wrapper, "arm_bash", command])
        {
            info.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException("setsid did not start.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return ToolResult.Error($"Could not start bash: {exception.Message}");
        }

        using (process)
        {
            var group = process.Id;
            context.Groups.Add(group);
            try
            {
                process.StandardInput.Close();
                var output = new CappedOutput();
                var readers = Task.WhenAll(ReadAsync(process.StandardOutput.BaseStream, output), ReadAsync(process.StandardError.BaseStream, output));

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
                var stopped = false;
                try
                {
                    await process.WaitForExitAsync(stop.Token);
                }
                catch (OperationCanceledException)
                {
                    stopped = true;
                    await ProcessGroups.StopAsync(group);
                    await process.WaitForExitAsync(CancellationToken.None);
                }

                // Output still held open by something the command left running (`sleep 100 &`)
                // would never end: give it a moment, then stop what's left.
                await Task.WhenAny(readers, Task.Delay(Drain, CancellationToken.None));
                if (ProcessGroups.IsAlive(group))
                {
                    await ProcessGroups.StopAsync(group);
                    await Task.WhenAny(readers, Task.Delay(Drain, CancellationToken.None));
                }

                var exitCode = process.ExitCode;
                var text = output.Total == 0 ? "(no output)" : output.ToString().TrimEnd('\n');
                if (!stopped)
                {
                    return ToolResult.Ok(string.Create(CultureInfo.InvariantCulture, $"{text}\n\nExit code: {exitCode}"));
                }

                return cancellationToken.IsCancellationRequested
                    ? ToolResult.Error($"{text}\n\nCancelled: the command and everything it started were stopped.")
                    : ToolResult.Error(string.Create(CultureInfo.InvariantCulture,
                        $"{text}\n\nTimed out after {timeoutSeconds} s: the command and everything it started were stopped."));
            }
            finally
            {
                context.Groups.Remove(group);
            }
        }
    }

    private static async Task ReadAsync(Stream stream, CappedOutput output)
    {
        var decoder = new UTF8Encoding(false).GetDecoder();
        var bytes = new byte[8192];
        var chars = new char[8192 + 4];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(bytes)) > 0)
            {
                var count = decoder.GetChars(bytes, 0, read, chars, 0, flush: false);
                output.Append(chars.AsSpan(0, count));
            }

            output.Append(chars.AsSpan(0, decoder.GetChars([], 0, 0, chars, 0, flush: true)));
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The pipe closed under us (the process disposed): what was read is the output.
        }
    }
}
