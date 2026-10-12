using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using Olve.AgentRuntimeManager.Supervisor.Protocol;

namespace Olve.AgentRuntimeManager.Supervisor;

/// <summary>
/// The lock on a shared copy (M5e): <c>&lt;copy&gt;.lock</c>, held with <c>flock</c> by whoever
/// clones, fetches or changes the copy's worktrees, across processes (a supervisor per session).
/// The kernel drops it with its holder, so a holder that died leaves no lock behind, only the file.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal static class RepoLock
{
    /// <summary>
    /// Takes the lock, polling until <paramref name="wait"/> has passed: null if it never came free
    /// (another holder kept it). Throws <see cref="OperationCanceledException"/> when
    /// <paramref name="cancel"/> fires, and <see cref="IOException"/> for anything else (it can't be
    /// opened or locked at all), which is that problem, not a busy lock.
    /// </summary>
    public static async Task<SafeFileHandle?> AcquireAsync(string path, TimeSpan wait, CancellationToken cancel, Action? waiting = null)
    {
        var handle = Posix.OpenLockFile(path);
        try
        {
            var deadline = DateTimeOffset.UtcNow + wait;
            var told = false;
            while (!Posix.TryLock(handle))
            {
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    handle.Dispose();
                    return null;
                }

                if (!told)
                {
                    waiting?.Invoke();
                    told = true;
                }

                await Task.Delay(100, cancel);
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }
}
