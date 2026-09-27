using System.Text.Json.Serialization;

namespace Olve.AgentRuntimeManager.Supervisor.Protocol;

/// <summary>
/// One line on the supervisor's socket (newline-delimited JSON), either way. Flat, so the protocol
/// can only grow: a <see cref="Type"/> plus the fields that type uses.
/// <list type="bullet">
/// <item>supervisor → ARM: <c>hello</c> (Version, RunId, SupervisorPid, AgentPid, OutputStart, StderrStart),
/// <c>output</c> (Line, End: the byte offset in <c>output.jsonl</c> just after it), <c>exited</c> (ExitCode).</item>
/// <item>ARM → supervisor: <c>attach</c> (Offset: send output from here on), <c>write</c> (Line, to the
/// agent's stdin), <c>closeInput</c>, <c>kill</c>.</item>
/// </list>
/// </summary>
public sealed record SupervisorMessage
{
    public const string Hello = "hello";
    public const string Output = "output";
    public const string Exited = "exited";
    public const string Attach = "attach";
    public const string Write = "write";
    public const string CloseInput = "closeInput";
    public const string Kill = "kill";

    public required string Type { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Version { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? RunId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SupervisorPid { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? AgentPid { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? OutputStart { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? StderrStart { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Line { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? End { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Offset { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ExitCode { get; init; }
}
