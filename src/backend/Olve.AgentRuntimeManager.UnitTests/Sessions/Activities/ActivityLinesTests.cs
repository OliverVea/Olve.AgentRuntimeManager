using System.Text.Json;
using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Sessions;
using Olve.AgentRuntimeManager.Sessions.Activities;
using Olve.AgentRuntimeManager.Sessions.Conversations;
using Olve.AgentRuntimeManager.Sessions.Providers.Claude;
using Olve.AgentRuntimeManager.Sessions.Supervision;

namespace Olve.AgentRuntimeManager.UnitTests.Sessions.Activities;

/// <summary>Which conversation entries become a session's activity, in what order, and how many.</summary>
public class ActivityLinesTests
{
    private static readonly SessionRecord Working = new()
    {
        Id = Guid.NewGuid(),
        Status = SessionStatus.Working,
        Prompt = "Fix it",
        Provider = "fake",
        Model = "fake",
        Caller = "tests",
        CreatedAt = DateTimeOffset.UnixEpoch,
        Tools = [],
        Skills = [],
    };

    private static readonly ConversationEntryRecord[] Conversation =
    [
        new(ConversationEntryKind.Prompt, Text: "Fix the build"),
        new(ConversationEntryKind.Thinking, Text: "Look first."),
        new(ConversationEntryKind.Text, Text: "Checking the build."),
        new(ConversationEntryKind.ToolCall, Tool: "Bash", Input: JsonSerializer.SerializeToElement(new { command = "cd /w && npx mise run ci" }), ToolId: "t1"),
        new(ConversationEntryKind.ToolResult, Text: "ok", ToolId: "t1", IsError: false),
        new(ConversationEntryKind.ToolCall, Tool: "Bash", Input: JsonSerializer.SerializeToElement(new { command = "git push" }), ToolId: "t2"),
        new(ConversationEntryKind.ToolResult, Text: "remote: no\nfatal: denied\nhint: ask", ToolId: "t2", IsError: true),
        new(ConversationEntryKind.Notice, Text: "Background command finished"),
        new(ConversationEntryKind.ToolCall, Tool: "Task", Input: JsonSerializer.SerializeToElement(new { description = "Look around" }), ToolId: "t3"),
        new(ConversationEntryKind.Text, Text: "A subagent's words.", ParentToolId: "t3"),
        new(ConversationEntryKind.ToolCall, Tool: "Read", Input: JsonSerializer.SerializeToElement(new { file_path = "a.cs" }), ToolId: "t4", ParentToolId: "t3"),
        new(ConversationEntryKind.ToolResult, Text: "error: gone", ToolId: "t4", IsError: true, ParentToolId: "t3"),
        new(ConversationEntryKind.ToolResult, Text: "Found it.", ToolId: "t3", IsError: false),
        new(ConversationEntryKind.Text, Text: "## Done\n\nThe **build** passes."),
        new(ConversationEntryKind.TurnEnd, Text: "Done", IsError: false),
        new(ConversationEntryKind.Prompt, Text: "Again"),
        new(ConversationEntryKind.TurnEnd, Text: "API Error: 500\nretry later", IsError: true),
    ];

    private static IReadOnlyList<(ActivityKind, string)> Pick(SessionRecord session, IEnumerable<ConversationEntryRecord> entries, int limit) =>
        [.. ActivityLines.Pick(session, entries.Reverse(), limit).Select(l => (l.Kind, l.Text))];

    [Test]
    public async Task EveryLine_OldestFirst_LeavesOutWhatIsNotActivity()
    {
        var lines = Pick(Working, Conversation, 50);

        // Prompts, thinking, notices, results and turn ends that didn't fail, and the subagent's entries are left out.
        await Assert.That(lines).IsEquivalentTo(
        [
            (ActivityKind.Text, "Checking the build."),
            (ActivityKind.Tool, "Bash → npx mise run ci"),
            (ActivityKind.Tool, "Bash → git push"),
            (ActivityKind.Error, "fatal: denied"),
            (ActivityKind.Tool, "Task → Look around"),
            (ActivityKind.Text, "Done"),
            (ActivityKind.Error, "API Error: 500"),
        ], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Limit_IsTheLatestLines()
    {
        await Assert.That(Pick(Working, Conversation, 3)).IsEquivalentTo(
            [(ActivityKind.Tool, "Task → Look around"), (ActivityKind.Text, "Done"), (ActivityKind.Error, "API Error: 500")],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(Pick(Working, Conversation, 1)).IsEquivalentTo([(ActivityKind.Error, "API Error: 500")]);
    }

    [Test]
    public async Task OnlyReadsAsFarBackAsItNeeds()
    {
        var read = 0;
        IEnumerable<ConversationEntryRecord> NewestFirst()
        {
            foreach (var entry in Conversation.Reverse())
            {
                read++;
                yield return entry;
            }
        }

        var lines = ActivityLines.Pick(Working, NewestFirst(), 2);

        await Assert.That(lines.Count).IsEqualTo(2);
        // The failed turn, the prompt, the turn end, then the text: four entries, not seventeen.
        await Assert.That(read).IsEqualTo(4);
    }

    [Test]
    public async Task NoConversation_NoLines()
    {
        await Assert.That(Pick(Working, [], 3)).IsEmpty();
    }

    [Test]
    public async Task FailedSession_EndsWithItsError_Once()
    {
        var failed = Working with { Status = SessionStatus.Failed, Error = "out of tokens" };
        ConversationEntryRecord[] entries =
        [
            new(ConversationEntryKind.Text, Text: "Reading."),
            new(ConversationEntryKind.TurnEnd, Text: "out of tokens", IsError: true),
        ];

        // Its last turn failed with the same error: shown once.
        await Assert.That(Pick(failed, entries, 3)).IsEquivalentTo(
            [(ActivityKind.Text, "Reading."), (ActivityKind.Error, "out of tokens")], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        // A different one shows too, before it.
        await Assert.That(Pick(failed with { Error = "Claude Code exited with code 1" }, entries, 3)).IsEquivalentTo(
            [(ActivityKind.Text, "Reading."), (ActivityKind.Error, "out of tokens"), (ActivityKind.Error, "Claude Code exited with code 1")],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
        // Without a conversation, and with room for one line only.
        await Assert.That(Pick(failed, [], 3)).IsEquivalentTo([(ActivityKind.Error, "out of tokens")]);
        await Assert.That(Pick(failed, entries, 1)).IsEquivalentTo([(ActivityKind.Error, "out of tokens")]);
    }

    [Test]
    public async Task OtherEndedSessions_HaveNoExtraLine()
    {
        var killed = Working with { Status = SessionStatus.Killed, Error = "left over" };

        await Assert.That(Pick(killed, [new(ConversationEntryKind.Text, Text: "Hi")], 3)).IsEquivalentTo([(ActivityKind.Text, "Hi")]);
    }

    [Test]
    public async Task FailuresWithoutText_SaySo()
    {
        ConversationEntryRecord[] entries =
        [
            new(ConversationEntryKind.ToolResult, Text: "", ToolId: "t1", IsError: true),
            new(ConversationEntryKind.TurnEnd, IsError: true),
            new(ConversationEntryKind.Text, Text: "\n  \n"),
            new(ConversationEntryKind.ToolCall, ToolId: "t2"),
        ];

        await Assert.That(Pick(Working, entries, 5)).IsEquivalentTo(
            [(ActivityKind.Error, "The tool call failed."), (ActivityKind.Error, "The turn failed."), (ActivityKind.Tool, "tool → {}")],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task ToolLine_IsOneBoundedLine()
    {
        var command = $"echo {new string('x', 400)}";
        ConversationEntryRecord[] entries =
        [
            new(ConversationEntryKind.ToolCall, Tool: "Bash", Input: JsonSerializer.SerializeToElement(new { description = "Two\nlines" })),
            new(ConversationEntryKind.ToolCall, Tool: "Bash", Input: JsonSerializer.SerializeToElement(new { command })),
        ];

        var lines = Pick(Working, entries, 5);

        await Assert.That(lines[0]).IsEqualTo((ActivityKind.Tool, "Bash → Two lines"));
        await Assert.That(lines[1].Item2.Length).IsEqualTo(ActivityText.MaxLength);
        await Assert.That(lines[1].Item2).StartsWith("Bash → echo xxx").And.EndsWith("x…");
    }

    [Test]
    public async Task NothingRecentWorthShowing_WithinTheScanBudget_IsNoLines()
    {
        // The agent said something, then a subagent ran for long: more output than one read scans.
        var folder = Directory.CreateTempSubdirectory("arm-activity-budget-").FullName;
        try
        {
            var output = Path.Combine(folder, "output.jsonl");
            var said = """{"type":"assistant","message":{"content":[{"type":"text","text":"Early words"}]},"parent_tool_use_id":null}""";
            var sub = """{"type":"assistant","message":{"content":[{"type":"text","text":"subagent busy"}]},"parent_tool_use_id":"t1"}""";
            async Task<IReadOnlyList<ActivityLine>> Activity(int subLines)
            {
                await File.WriteAllTextAsync(output, said + "\n" + string.Concat(Enumerable.Repeat(sub + "\n", subLines)));
                return ActivityLines.Pick(Working, ClaudeConversation.ReadNewestFirst(OutputLines.Backwards(output)), 3);
            }

            await Assert.That((await Activity(100)).Select(l => l.Text)).IsEquivalentTo(["Early words"]);
            await Assert.That(await Activity((OutputLines.MaxScanBytes / sub.Length) + 10)).IsEmpty();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public async Task HugeToolInput_IsOneBoundedLine()
    {
        var description = new string('d', 3_000_000);
        ConversationEntryRecord[] entries = [new(ConversationEntryKind.ToolCall, Tool: "Bash", Input: JsonSerializer.SerializeToElement(new { description }))];

        var line = Pick(Working, entries, 1).Single();

        await Assert.That(line.Item2.Length).IsEqualTo(ActivityText.MaxLength);
    }
}
