namespace Olve.AgentRuntimeManager.Sessions.Repos;

/// <summary>Settings of the sessions' git checkouts (configuration section <c>Repos</c>; M5e).</summary>
public sealed class RepoOptions
{
    public const string Section = "Repos";

    /// <summary>
    /// Where the shared bare copy of each repository is kept (<c>&lt;root&gt;/&lt;name&gt;-&lt;hash of the URL&gt;.git</c>),
    /// written by the agents' user. Empty: <c>olve-arm/repos</c> in the user's local data folder,
    /// next to the sessions' default work root.
    /// </summary>
    public string Root { get; set; } = "";

    /// <summary>
    /// The longest one git command of a checkout (a clone's fetch, mostly) may take; then the
    /// checkout fails, and with it the session. A session waiting for another's checkout of the same
    /// repository waits up to four times this.
    /// </summary>
    public TimeSpan GitTimeout { get; set; } = TimeSpan.FromMinutes(5);
}
