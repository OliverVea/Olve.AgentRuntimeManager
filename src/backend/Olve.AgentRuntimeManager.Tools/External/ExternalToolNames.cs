using System.Text;
using Olve.AgentRuntimeManager.Tools.Protocol;

namespace Olve.AgentRuntimeManager.Tools.External;

/// <summary>
/// What a server's tools are called towards the agent: <c>&lt;server&gt;__&lt;tool&gt;</c>, made safe here
/// rather than left to the provider, since other providers may not.
/// </summary>
internal static class ExternalToolNames
{
    /// <summary>Claude Code silently drops a tool whose full name passes this (spiked, M10c).</summary>
    private const int MaxFullName = 128;

    private const string ProviderPrefix = $"mcp__{ToolsLaunch.ServerName}__";

    /// <summary>The longest exported name.</summary>
    public static readonly int MaxLength = MaxFullName - ProviderPrefix.Length;

    public static string Export(string server, string tool)
    {
        var name = new StringBuilder(server.Length + ToolsLaunch.ServerToolSeparator.Length + tool.Length)
            .Append(server)
            .Append(ToolsLaunch.ServerToolSeparator);
        foreach (var c in tool)
        {
            name.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        }

        return name.ToString();
    }

    /// <summary>Why <paramref name="exported"/> can't be used as it is; null if it can.</summary>
    public static string? Problem(string tool, string exported) =>
        exported.Length > MaxLength
            ? $"its tool '{tool}' has too long a name: {exported} is {exported.Length} characters, and the limit is {MaxLength}."
            : null;
}
