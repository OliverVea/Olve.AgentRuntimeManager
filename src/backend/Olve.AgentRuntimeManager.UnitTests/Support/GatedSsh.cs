namespace Olve.AgentRuntimeManager.UnitTests.Support;

/// <summary>
/// A slow transport for checkout tests (M5e), with no network and no hooks: ssh URLs
/// (<c>ssh://arm.invalid/&lt;local path&gt;</c>) whose "ssh" (<c>GIT_SSH_COMMAND</c>, which git runs
/// through the shell) marks <see cref="Started"/>, waits until the test calls <see cref="Release"/>,
/// then runs the remote command locally. So a test holds a clone or fetch for as long as it likes.
/// </summary>
public sealed class GatedSsh(string folder)
{
    private readonly string _release = Path.Combine(folder, $"release-{Guid.NewGuid():N}");
    private readonly string _started = Path.Combine(folder, $"started-{Guid.NewGuid():N}");

    /// <summary>The session environment that routes git through the gate.</summary>
    public IReadOnlyDictionary<string, string> Env => new Dictionary<string, string>
    {
        ["GIT_SSH_COMMAND"] = $"gate() {{ : > '{_started}'; while [ ! -e '{_release}' ]; do sleep 0.05; done; shift; sh -c \"$*\"; }}; gate",
        // Just the host and the command: no option probing of an unknown ssh.
        ["GIT_SSH_VARIANT"] = "simple",
    };

    /// <summary>A repository at a local path, reached through the gate.</summary>
    public static string Url(string path) => $"ssh://arm.invalid{path}";

    /// <summary>Whether git has reached the gate (a clone or fetch is waiting on it).</summary>
    public bool Started => File.Exists(_started);

    /// <summary>A fragment of the waiting shell's command line, to find it (and see it's gone).</summary>
    public string Fragment => _release;

    public void Release() => File.WriteAllText(_release, "");

    /// <summary>Waits until git has reached the gate (bounded).</summary>
    public async Task ReachedAsync(TimeSpan guard)
    {
        using var timeout = new CancellationTokenSource(guard);
        while (!Started)
        {
            await Task.Delay(20, timeout.Token);
        }
    }
}
