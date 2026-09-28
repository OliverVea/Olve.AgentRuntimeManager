using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Olve.AgentRuntimeManager.Supervisor.Protocol;

/// <summary>
/// The files a supervisor keeps in its session's folder. <c>output.jsonl</c> and <c>stderr.log</c>
/// hold the agent's raw output (every run of the session, appended); <c>supervisor.json</c> and
/// <c>exit.json</c> describe the latest run.
/// </summary>
public static class SupervisorFiles
{
    public const string Output = "output.jsonl";
    public const string Stderr = "stderr.log";
    public const string Info = "supervisor.json";
    public const string Exit = "exit.json";
    public const string Log = "supervisor.log";

    public static SupervisorInfo? ReadInfo(string folder) => Read(Path.Combine(folder, Info), SupervisorJsonContext.Default.SupervisorInfo);

    public static SupervisorExit? ReadExit(string folder) => Read(Path.Combine(folder, Exit), SupervisorJsonContext.Default.SupervisorExit);

    /// <summary>Writes a whole file or nothing (temp file, then rename), so a reader never sees half of it.</summary>
    public static void WriteAtomically<T>(string path, T value, JsonTypeInfo<T> type)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(value, type));
        File.Move(temp, path, overwrite: true);
    }

    private static T? Read<T>(string path, JsonTypeInfo<T> type) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllBytes(path), type);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
