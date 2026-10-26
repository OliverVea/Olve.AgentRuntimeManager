using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Sessions;
using Olve.AgentRuntimeManager.Sessions.Activities;
using Olve.AgentRuntimeManager.Sessions.Conversations;
using Olve.AgentRuntimeManager.Sessions.Providers.Claude;
using TUnit.Assertions.Enums;

namespace Olve.AgentRuntimeManager.UnitTests.Sessions.Claude;

/// <summary>
/// The conversation and activity of an agent with ARM's tools (M10), from real output (the stdio
/// tools spike, Claude Code 2.1.310, paths scrubbed): <c>arm-tools.jsonl</c> calls one tool,
/// <c>arm-tools-long.jsonl</c> one that takes 650 s, with Claude's heartbeats every 30 s meanwhile.
/// </summary>
public class ClaudeArmToolsConversationTests
{
    private static readonly SessionRecord Working = new()
    {
        Id = Guid.NewGuid(),
        Status = SessionStatus.Working,
        Prompt = "Probe",
        Provider = "claude",
        Model = "sonnet",
        Caller = "tests",
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    [Test]
    public async Task ArmsTools_ShowByArmsName_WithoutTheirServersPrefix()
    {
        var entries = ClaudeConversation.Read(Recorded("arm-tools.jsonl"));

        var call = entries.Single(e => e.Kind == ConversationEntryKind.ToolCall);
        await Assert.That(call.Tool).IsEqualTo("arm_probe");
        await Assert.That(call.ToolId).IsEqualTo("toolu_01M7n1NxRatENY9zM7vBE3cQ");
        await Assert.That(entries.Single(e => e.Kind == ConversationEntryKind.ToolResult).ToolId).IsEqualTo(call.ToolId);
    }

    [Test]
    public async Task ArmsTools_InTheActivity_ShowByArmsName()
    {
        var lines = ActivityLines.Pick(Working, ClaudeConversation.ReadNewestFirst(Enumerable.Reverse(Recorded("arm-tools.jsonl"))), 5);

        await Assert.That(lines.First(l => l.Kind == ActivityKind.Tool).Text).IsEqualTo("""arm_probe → {"note":"hi"}""");
    }

    [Test]
    public async Task Heartbeats_DuringALongCall_AreLeftOut_AndNotReportedAsUnknown()
    {
        var lines = Recorded("arm-tools-long.jsonl");
        var unknown = new List<string>();

        var entries = ClaudeConversation.Read(lines, unknown.Add);
        var newestFirst = ClaudeConversation.ReadNewestFirst(Enumerable.Reverse(lines), unknown.Add).ToList();

        // 21 heartbeats in the recording; none of them is an entry.
        await Assert.That(lines.Count(l => l.Contains("\"heartbeat\":true", StringComparison.Ordinal))).IsEqualTo(21);
        await Assert.That(entries.Select(e => e.Kind)).IsEquivalentTo(
            [ConversationEntryKind.ToolCall, ConversationEntryKind.ToolResult, ConversationEntryKind.Text, ConversationEntryKind.TurnEnd], CollectionOrdering.Matching);
        await Assert.That(entries.Single(e => e.Kind == ConversationEntryKind.ToolCall).Tool).IsEqualTo("arm_sleep");
        await Assert.That(entries.Single(e => e.Kind == ConversationEntryKind.ToolResult).Text).IsEqualTo("slept 650s");
        await Assert.That(newestFirst.Select(e => e.Kind)).IsEquivalentTo(entries.Select(e => e.Kind).Reverse(), CollectionOrdering.Matching);
        await Assert.That(unknown).IsEmpty();
        var activity = ActivityLines.Pick(Working, newestFirst, 50);
        await Assert.That(activity.Select(l => l.Text)).IsEquivalentTo(["""arm_sleep → {"seconds":650}""", "slept 650s"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("mcp__arm__arm_bash", "arm_bash")]
    [Arguments("Bash", "Bash")]
    [Arguments("mcp__other__arm_bash", "mcp__other__arm_bash")]
    [Arguments("mcp__arm__", "mcp__arm__")]
    public async Task OnlyArmsServersPrefix_IsStripped(string name, string shown) =>
        await Assert.That(ClaudeConversation.ToolName(name)).IsEqualTo(shown);

    private static string[] Recorded(string name) =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Sessions", "Claude", "Stub", name));
}
