using System.Text.RegularExpressions;

namespace Olve.AgentRuntimeManager.Supervisor.Protocol;

/// <summary>
/// Where Claude Code keeps a session's transcripts: <c>&lt;config&gt;/projects/&lt;working directory&gt;</c>,
/// the directory's path with every character but letters and digits made <c>-</c>
/// (<c>/var/lib/olve-arm/sessions/&lt;id&gt;/work</c> → <c>-var-lib-olve-arm-sessions-&lt;id&gt;-work</c>).
/// A session's workplace is its own, so that folder holds only its transcripts (#22).
/// </summary>
public static partial class ClaudeProjects
{
    /// <summary>The folder's name for an agent that ran in <paramref name="workplace"/> (a full path).</summary>
    public static string FolderName(string workplace) => NotLetterOrDigit().Replace(workplace, "-");

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NotLetterOrDigit();
}
