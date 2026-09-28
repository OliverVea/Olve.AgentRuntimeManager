using System.Diagnostics;

namespace Olve.AgentRuntimeManager.Sessions.Supervision;

/// <summary>
/// The user agents run as when it isn't ARM's own (<see cref="SupervisorOptions.User"/>;
/// docs/AGENT-USER.md): its name and home, from the system's user database.
/// </summary>
public sealed record AgentAccount(string Name, string Home)
{
    /// <summary>
    /// The agent's environment as that user: its own <c>HOME</c> (where Claude Code keeps its state,
    /// mise its toolchains, ssh its keys), <c>USER</c> and <c>LOGNAME</c>, instead of ARM's.
    /// </summary>
    public IReadOnlyDictionary<string, string> Identify(IReadOnlyDictionary<string, string> environment) =>
        new Dictionary<string, string>(environment, StringComparer.Ordinal)
        {
            ["HOME"] = Home,
            ["USER"] = Name,
            ["LOGNAME"] = Name,
        };

    /// <summary>
    /// Looks <paramref name="user"/> up (<c>getent passwd</c>, so any user database the system has).
    /// Empty: null, agents run as ARM's user. Throws if there's no such user: that's a broken
    /// deployment, and no agent should start as ARM's user instead.
    /// </summary>
    public static AgentAccount? Find(string user)
    {
        if (string.IsNullOrWhiteSpace(user))
        {
            return null;
        }

        var info = new ProcessStartInfo("getent") { RedirectStandardOutput = true, UseShellExecute = false };
        info.ArgumentList.Add("passwd");
        info.ArgumentList.Add(user);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("'getent' did not start.");
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 && Parse(output) is { } account && account.Name == user
            ? account
            : throw new InvalidOperationException($"The agent user '{user}' (Supervisor:User) does not exist.");
    }

    /// <summary>A <c>passwd</c> line (<c>name:password:uid:gid:gecos:home:shell</c>); null if it isn't one.</summary>
    public static AgentAccount? Parse(string line)
    {
        var fields = line.Trim().Split(':');
        return fields is [{ Length: > 0 } name, _, _, _, _, { Length: > 0 } home, _] ? new AgentAccount(name, home) : null;
    }
}
