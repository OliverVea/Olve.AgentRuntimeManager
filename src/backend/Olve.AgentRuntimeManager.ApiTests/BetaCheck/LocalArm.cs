using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Olve.AgentRuntimeManager.UnitTests.Support;

namespace Olve.AgentRuntimeManager.ApiTests.BetaCheck;

/// <summary>What a run of the beta check printed, and how it ended.</summary>
public sealed record CheckRun(int ExitCode, string Output)
{
    public override string ToString() => $"exit code {ExitCode}:\n{Output}";
}

/// <summary>
/// ARM as the beta check meets it on beta, locally: this build as a process of its own on a free
/// port, configured through its environment as <c>/etc/olve-arm/env</c> configures it in the VM
/// (with a test signing key, and a database and session folders of its own), with the stub Claude
/// Code (<c>claude-stub.py</c>) behaving as its mode says. <see cref="RunCheckAsync"/> runs the
/// check (<c>.pipelines/scripts/beta-check.py</c>) against it over HTTP, as test-after-beta does.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed partial class LocalArm : IAsyncDisposable
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

    /// <summary>The repository the check's claude session is created with: a local one (the URL is its path).</summary>
    public string Repo => Path.Combine(_root, "origin", "beta-check-repo");

    /// <summary>A client with a valid token.</summary>
    public HttpClient Client()
    {
        var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_port}") };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Tokens.Mint());
        return client;
    }

    /// <summary>
    /// Starts ARM with the stub Claude Code in <paramref name="mode"/> (see claude-stub.py) and
    /// <paramref name="settings"/> (<c>NAME=value</c>, its environment); returns once it's healthy.
    /// </summary>
    public static async Task<LocalArm> StartAsync(string mode = "ok", params string[] settings)
    {
        var root = Directory.CreateTempSubdirectory("arm-beta-check-").FullName;
        TestGit.Origin(Path.Combine(root, "origin", "beta-check-repo"));
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
        info.Environment["Repos__Root"] = Path.Combine(root, "repos");
        info.Environment["Arm__Version"] = Version;
        info.Environment["Arm__Environment"] = "beta";
        info.Environment["OpenTelemetry__Endpoint"] = "";
        info.Environment["CLAUDE_CODE_OAUTH_TOKEN"] = "stub";
        foreach (var setting in settings)
        {
            info.Environment[setting.Split('=', 2)[0]] = setting.Split('=', 2)[1];
        }

        // In a process group of its own: ended through it, nothing looked for.
        var process = Process.Start(TestProcesses.InOwnGroup(info)) ?? throw new InvalidOperationException("ARM did not start.");
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
    /// Runs the check as test-after-beta runs it, from outside, over HTTP only (<c>python3 -u
    /// beta-check.py</c>, its settings in its environment), with a minted token instead of the
    /// machine client's; <paramref name="settings"/> (<c>NAME=value</c>) override the others.
    /// </summary>
    public async Task<CheckRun> RunCheckAsync(params string[] settings)
    {
        var info = new ProcessStartInfo("python3", ["-u", Path.Combine(Here, "beta-check.py")])
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.Environment["BETA_CHECK_API"] = $"http://127.0.0.1:{_port}";
        info.Environment["BETA_CHECK_VERSION"] = Version;
        info.Environment["BETA_CHECK_TOKEN"] = Tokens.Mint();
        // Here ARM, its agents and the check are all this user (root, in the pipeline's gate).
        info.Environment["BETA_CHECK_AGENT_USER"] = User;
        info.Environment["BETA_CHECK_NOT_AGENT_USERS"] = "";
        info.Environment["BETA_CHECK_TURN_SECONDS"] = "20";
        info.Environment["BETA_CHECK_CHECKOUT_SECONDS"] = "20";
        // A local repository for the claude session's checkout, as beta's clones this one.
        info.Environment["BETA_CHECK_REPO_URL"] = Repo;
        foreach (var setting in settings)
        {
            var (name, value) = (setting.Split('=', 2)[0], setting.Split('=', 2)[1]);
            info.Environment[name] = value;
        }

        using var process = Process.Start(TestProcesses.InOwnGroup(info)) ?? throw new InvalidOperationException("python3 did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(CheckTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            TestProcesses.KillOwnGroup(process);
            throw new TimeoutException($"The beta check took over {CheckTimeout.TotalSeconds}s:\n{await output}{await errors}");
        }

        return new CheckRun(process.ExitCode, await output + await errors);
    }

    /// <summary>The sessions the check's caller has, with their status and provider.</summary>
    public async Task<List<(string Id, string Status, string Provider)>> CheckSessionsAsync()
    {
        using var client = Client();
        using var response = await client.PostAsJsonAsync("/api/sessions/search", new { caller = "test-after-beta", limit = 100 });
        response.EnsureSuccessStatusCode();
        using var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return [.. page.RootElement.GetProperty("items").EnumerateArray()
            .Select(s => (s.GetProperty("id").GetString()!, s.GetProperty("status").GetString()!, s.GetProperty("provider").GetString()!))];
    }

    /// <summary>The worktrees of the shared copies of the repositories, but for the copies themselves (listed first): none once their sessions are deleted.</summary>
    public IReadOnlyList<string> Worktrees()
    {
        var repos = Path.Combine(_root, "repos");
        return Directory.Exists(repos)
            ? [.. Directory.GetDirectories(repos, "*.git").SelectMany(shared => TestGit.Run(shared, "worktree", "list", "--porcelain")
                .Split('\n').Where(line => line.StartsWith("worktree ", StringComparison.Ordinal)).Skip(1))]
            : [];
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
        // ARM stopped first, so it neither starts another supervisor nor reaps one: those it started
        // (named in its log) stay provably its children while their groups are killed. Then ARM's own
        // group, and what's left of a run whose supervisor was gone already (its agent, by supervisor.json).
        TestProcesses.StopOwnGroup(_process);
        TestProcesses.KillSupervisors(Sessions, StartedSupervisors(), _process.Id);
        TestProcesses.KillOwnGroup(_process);
        try
        {
            await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            // The kill is under way.
        }

        TestProcesses.KillSupervisors(Sessions, []);

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

    /// <summary>Some of what <c>/etc/olve-arm/env</c> sets in the VM (<c>Auth__Authority</c> must not reach an agent).</summary>
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

    /// <summary>The supervisors ARM said it started ("supervisor {Pid} started"), from its log.</summary>
    private List<int> StartedSupervisors()
    {
        lock (_log)
        {
            return [.. StartedLine().Matches(_log.ToString()).Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))];
        }
    }

    [GeneratedRegex(@"supervisor (\d+) started for run")]
    private static partial Regex StartedLine();

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
