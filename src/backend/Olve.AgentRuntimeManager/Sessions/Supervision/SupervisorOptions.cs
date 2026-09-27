namespace Olve.AgentRuntimeManager.Sessions.Supervision;

/// <summary>Settings of the per-session supervisors (configuration section <c>Supervisor</c>; docs/GENTLE-RESTART.md).</summary>
public sealed class SupervisorOptions
{
    public const string Section = "Supervisor";

    /// <summary>The supervisor executable. Empty: <c>olve-arm-supervisor</c> next to ARM's own.</summary>
    public string Command { get; set; } = "";

    /// <summary>
    /// Where the supervisors' sockets are (<c>&lt;root&gt;/&lt;session id&gt;.sock</c>). Empty:
    /// <c>$XDG_RUNTIME_DIR/olve-arm</c>, else <c>olve-arm-&lt;user&gt;</c> in the temp folder.
    /// Keep it short: a socket path has at most 107 bytes.
    /// </summary>
    public string SocketRoot { get; set; } = "";

    /// <summary>
    /// The user the supervisors, and with them the agents, run as (docs/AGENT-USER.md). Empty: ARM's
    /// own user, as in local dev and tests. Set: ARM starts each supervisor with
    /// <c>sudo -n -u &lt;user&gt;</c>, which needs a sudoers rule for exactly the supervisor, and ARM a
    /// member of the user's group, which must own the sessions' work root and socket root (setgid).
    /// The deployment sets this up (src/deploy/vm/vm-deploy.sh).
    /// </summary>
    public string User { get; set; } = "";

    /// <summary>The <c>sudo</c> that starts supervisors as <see cref="User"/> (a name on <c>PATH</c>, or a path).</summary>
    public string Sudo { get; set; } = "sudo";

    /// <summary>How long a new supervisor may take to answer, and how long a lost one is waited for.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
