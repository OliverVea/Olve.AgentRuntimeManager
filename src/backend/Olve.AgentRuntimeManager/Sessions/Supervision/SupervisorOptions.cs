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

    /// <summary>How long a new supervisor may take to answer, and how long a lost one is waited for.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
