using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Olve.AgentRuntimeManager.Supervisor.Protocol;
using Olve.AgentRuntimeManager.UnitTests.Support;

namespace Olve.AgentRuntimeManager.ApiTests.BetaCheck;

/// <summary>What a run of the beta check printed, and how it ended.</summary>
public sealed record CheckRun(int ExitCode, string Output)
{
    public override string ToString() => $"exit code {ExitCode}:\n{Output}";
}

/// <summary>
/// ARM as the beta check meets it in the VM, locally: this build as a process of its own on a free
/// port, configured through its environment as <c>/etc/olve-arm/env</c> configures it in the VM
/// (with a test signing key, and a database and session folders of its own), with the stub Claude
/// Code (<c>claude-stub.py</c>) behaving as its mode says. <see cref="RunCheckAsync"/> runs the
/// check (<c>src/deploy/vm/beta-check.py</c>) against it, as the VM's <c>arm</c> would.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class LocalArm : IAsyncDisposable
{
    public const string Version = "2026.10.12.1";

    /// <summary>How long one run of the check may take here (its waits are bounded too).</summary>
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(90);

    private static readonly TestTokens Tokens = new(
        SigningKey: "beta-check-test-signing-key-that-is-long-enough-for-hs256",
        Issuer: "beta-check-test",
        Audience: "olve-arm");

    private static string Here => Path.Combine(AppContext.BaseDirectory, "BetaCheck");

    private readonly string _root;
    private readonly int _port;
    private readonly Process _process;
    private readonly StringBuilder _log = new();

    private LocalArm(string root, int port, Process process)
    {
        _root = root;
        _port = port;
        _process = process;
    }

    /// <summary>The user the check (and here ARM and its agents) runs as.</summary>
    public static string User => Environment.UserName;

    public string Sessions => Path.Combine(_root, "sessions");

    /// <summary>A client with a valid token.</summary>
    public HttpClient Client()
    {
        var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Tokens.Mint());
        return client;
    }

    /// <summary>Starts ARM with the stub Claude Code in <paramref name="mode"/> (see claude-stub.py); returns once it's healthy.</summary>
    public static async Task<LocalArm> StartAsync(string mode = "ok")
    {
        var root = Directory.CreateTempSubdirectory("arm-beta-check-").FullName;
        var stub = Path.Combine(root, "claude");
        File.Copy(Path.Combine(Here, "claude-stub.py"), stub);
        File.SetUnixFileMode(stub, File.GetUnixFileMode(stub) | UnixFileMode.UserExecute);
        await File.WriteAllTextAsync(Path.Combine(root, "stub-mode"), mode);

        var port = FreePort();
        // `dotnet test` names the dotnet it runs on; the apphost might not find a runtime of its own.
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = root, // no appsettings*.json here: only what's set below
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Olve.AgentRuntimeManager.dll"));
        foreach (var (name, value) in Config(root, port))
        {
            info.Environment[name] = value;
        }

        info.Environment["Auth__SigningKey"] = Tokens.SigningKey;
        info.Environment["ConnectionStrings__Arm"] = ApiFactory.ConnectionString(root);
        info.Environment["Providers__Claude__Command"] = stub;
        info.Environment["Supervisor__SocketRoot"] = Path.Combine(root, "sockets");
        info.Environment["Arm__Version"] = Version;
        info.Environment["Arm__Environment"] = "beta";
        info.Environment["OpenTelemetry__Endpoint"] = "";
        info.Environment["CLAUDE_CODE_OAUTH_TOKEN"] = "stub";

        var process = Process.Start(info) ?? throw new InvalidOperationException("ARM did not start.");
        var arm = new LocalArm(root, port, process);
        process.OutputDataReceived += (_, e) => arm.Log(e.Data);
        process.ErrorDataReceived += (_, e) => arm.Log(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await arm.WaitHealthyAsync();
        }
        catch
        {
            await arm.DisposeAsync();
            throw;
        }

        return arm;
    }

    /// <summary>
    /// Runs the check as deploy-beta runs it in the VM (<c>python3 -u -</c>, the script on stdin
    /// after a <c>PRESET</c> of its settings), with a minted token instead of the machine client's
    /// secret; <paramref name="settings"/> (<c>NAME=value</c>) override the others.
    /// </summary>
    public async Task<CheckRun> RunCheckAsync(params string[] settings)
    {
        var envFile = Path.Combine(_root, "env");
        await File.WriteAllLinesAsync(envFile, Config(_root, _port).Select(c => $"{c.Name}={c.Value}"));

        var info = new ProcessStartInfo("python3", ["-u", "-"])
        {
            WorkingDirectory = _root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.Environment["BETA_CHECK_ENV_FILE"] = envFile;
        // Here ARM, its agents and the check are all this user.
        info.Environment["BETA_CHECK_AGENT_USER"] = User;
        info.Environment["BETA_CHECK_SAME_USER"] = "1";
        info.Environment["BETA_CHECK_TURN_SECONDS"] = "20";
        foreach (var setting in settings)
        {
            var (name, value) = (setting.Split('=', 2)[0], setting.Split('=', 2)[1]);
            info.Environment[name] = value;
        }

        var preset = new Dictionary<string, string> { ["BETA_CHECK_VERSION"] = Version, ["BETA_CHECK_TOKEN"] = Tokens.Mint() };
        foreach (var name in preset.Keys.Where(info.Environment.ContainsKey).ToList())
        {
            preset[name] = info.Environment[name]!;
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("python3 did not start.");
        await process.StandardInput.WriteLineAsync($"PRESET = {JsonSerializer.Serialize(preset)}");
        await process.StandardInput.WriteAsync(await File.ReadAllTextAsync(Path.Combine(Here, "beta-check.py")));
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(CheckTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"The beta check took over {CheckTimeout.TotalSeconds}s:\n{await output}{await errors}");
        }

        return new CheckRun(process.ExitCode, await output + await errors);
    }

    /// <summary>The sessions the check's caller has, by status.</summary>
    public async Task<List<(string Id, string Status)>> CheckSessionsAsync()
    {
        using var client = Client();
        using var response = await client.PostAsJsonAsync("/api/sessions/search", new { caller = "deploy-beta", limit = 100 });
        response.EnsureSuccessStatusCode();
        using var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return [.. page.RootElement.GetProperty("items").EnumerateArray()
            .Select(s => (s.GetProperty("id").GetString()!, s.GetProperty("status").GetString()!))];
    }

    /// <summary>The registered environment variables, by name.</summary>
    public async Task<Dictionary<string, string>> EnvAsync()
    {
        using var client = Client();
        using var list = JsonDocument.Parse(await client.GetStringAsync("/api/env"));
        return list.RootElement.EnumerateArray().ToDictionary(v => v.GetProperty("name").GetString()!, v => v.GetProperty("value").GetString()!);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException)
        {
            // Already gone, or the kill is under way.
        }

        TestProcesses.KillSupervisors(Sessions);
        KillLeftovers();

        _process.Dispose();
        lock (_log)
        {
            Console.WriteLine($"ARM's log:\n{_log}");
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover temp folder is harmless.
        }
    }

    /// <summary>The settings the check reads from ARM's config file, which ARM gets as its environment.</summary>
    private static (string Name, string Value)[] Config(string root, int port) =>
    [
        ("Host", "127.0.0.1"),
        ("Port", port.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ("Auth__Authority", Tokens.Issuer),
        ("Auth__Audience", Tokens.Audience),
        ("Providers__Claude__WorkRoot", Path.Combine(root, "sessions")),
    ];

    private void Log(string? line)
    {
        if (line is not null)
        {
            lock (_log)
            {
                _log.AppendLine(line);
            }
        }
    }

    private async Task WaitHealthyAsync()
    {
        using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}"), Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && !_process.HasExited)
        {
            try
            {
                using var health = await client.GetAsync("/health");
                if (health.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                // Not listening yet.
            }

            await Task.Delay(100);
        }

        lock (_log)
        {
            throw new InvalidOperationException($"ARM didn't become healthy on port {_port}:\n{_log}");
        }
    }

    /// <summary>
    /// Kills whatever still runs in this ARM's folders (its sessions' workplaces): what a kill left
    /// behind, or what the stub's `leak` mode let escape the agent's process tree.
    /// </summary>
    private void KillLeftovers()
    {
        foreach (var folder in Directory.EnumerateDirectories("/proc"))
        {
            try
            {
                if (int.TryParse(Path.GetFileName(folder), out var pid) && pid != Environment.ProcessId
                    && File.ResolveLinkTarget(Path.Combine(folder, "cwd"), returnFinalTarget: false)?.FullName is { } cwd
                    && (cwd == _root || cwd.StartsWith(_root + "/", StringComparison.Ordinal)))
                {
                    Posix.Kill(pid, Posix.SigKill);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Gone, or not ours.
            }
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
