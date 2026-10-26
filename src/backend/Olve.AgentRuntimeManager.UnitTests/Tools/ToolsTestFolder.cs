using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Olve.AgentRuntimeManager.Tools.Calls;
using Olve.AgentRuntimeManager.Tools.Tools;
using Olve.AgentRuntimeManager.UnitTests.Support;

namespace Olve.AgentRuntimeManager.UnitTests.Tools;

/// <summary>
/// A test's own workplace for ARM's tools (a temporary folder), the context calls run with, and
/// arguments from JSON. Disposing it deletes the folder.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class ToolsTestFolder : IDisposable
{
    public string Workplace { get; } = Directory.CreateTempSubdirectory("arm-tools-").FullName;

    internal ProcessGroups Groups { get; } = new();

    internal ToolContext Context => new(Workplace, Groups);

    public string PathOf(string relative) => Path.Combine(Workplace, relative);

    public string Write(string relative, string content)
    {
        var path = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    internal static ToolArguments Arguments(string json) => new(JsonNode.Parse(json) as JsonObject);

    /// <summary>A name for a test's processes to carry (<c>exec -a</c>), to see they're gone (<see cref="TestProcesses.WithCommandLine"/>).</summary>
    public static string Marker() => $"arm-tools-test-{Guid.NewGuid():N}";

    /// <summary>Whether no process carries <paramref name="marker"/>, within a few seconds (a killed one may take a moment).</summary>
    public static async Task<bool> Gone(string marker)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (TestProcesses.WithCommandLine(marker).Count > 0)
        {
            if (DateTime.UtcNow > deadline)
            {
                return false;
            }

            await Task.Delay(50);
        }

        return true;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Workplace, recursive: true);
        }
        catch (IOException)
        {
            // Something a test left running may still write there; the temp folder is the OS's to clear.
        }
    }
}
