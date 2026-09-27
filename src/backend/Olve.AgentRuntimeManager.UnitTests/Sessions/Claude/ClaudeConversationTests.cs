using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Sessions.Providers.Claude;

namespace Olve.AgentRuntimeManager.UnitTests.Sessions.Claude;

/// <summary>A session's conversation read from Claude Code's stream-json output.</summary>
public class ClaudeConversationTests
{
    [Test]
    public async Task RecordedTurn_IsItsTextAndItsEnd()
    {
        var lines = await File.ReadAllLinesAsync(Path.Combine(AppContext.BaseDirectory, "Sessions", "Claude", "Stub", "success.jsonl"));

        var entries = ClaudeConversation.Read(lines);

        // The init event, rate limits and a thinking block without text are left out.
        await Assert.That(entries.Select(e => e.Kind)).IsEquivalentTo([ConversationEntryKind.Text, ConversationEntryKind.TurnEnd], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(entries[0].Text).IsEqualTo("READY\n\nNO-CANARY");
        await Assert.That(entries[1].IsError).IsFalse();
    }

    [Test]
    public async Task EveryKind_InOrder()
    {
        string[] lines =
        [
            """{"type":"system","subtype":"init","tools":["Bash"]}""",
            """{"type":"user","message":{"role":"user","content":"Fix the build"},"parent_tool_use_id":null,"timestamp":"2026-09-27T10:00:00Z"}""",
            """{"type":"assistant","message":{"content":[{"type":"thinking","thinking":"Look first."},{"type":"text","text":"Checking."},{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"ls"}}]},"parent_tool_use_id":null}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t1","content":"a.txt\nb.txt","is_error":false}]},"parent_tool_use_id":null}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"t2","content":[{"type":"text","text":"no such file"},{"type":"image"}],"is_error":true}]},"parent_tool_use_id":null}""",
            """{"type":"assistant","message":{"content":[{"type":"text","text":"Sub-step."}]},"parent_tool_use_id":"t9"}""",
            """{"type":"result","subtype":"success","is_error":false,"result":"Fixed."}""",
        ];

        var entries = ClaudeConversation.Read(lines);

        await Assert.That(entries.Select(e => e.Kind)).IsEquivalentTo(
        [
            ConversationEntryKind.Prompt, ConversationEntryKind.Thinking, ConversationEntryKind.Text, ConversationEntryKind.ToolCall,
            ConversationEntryKind.ToolResult, ConversationEntryKind.ToolResult, ConversationEntryKind.Text, ConversationEntryKind.TurnEnd,
        ], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(entries[0].Text).IsEqualTo("Fix the build");
        await Assert.That(entries[0].At).IsEqualTo(new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero));
        await Assert.That(entries[3].Tool).IsEqualTo("Bash");
        await Assert.That(entries[3].ToolId).IsEqualTo("t1");
        await Assert.That(entries[3].Input!.Value.GetProperty("command").GetString()).IsEqualTo("ls");
        await Assert.That(entries[4]).IsEqualTo(entries[4] with { Text = "a.txt\nb.txt", ToolId = "t1", IsError = false });
        await Assert.That(entries[5]).IsEqualTo(entries[5] with { Text = "no such file\n[image]", ToolId = "t2", IsError = true });
        await Assert.That(entries[6].ParentToolId).IsEqualTo("t9");
        await Assert.That(entries[7]).IsEqualTo(entries[7] with { Text = "Fixed.", IsError = false });
    }

    [Test]
    public async Task UserMessage_AsTextBlocks_IsAPrompt()
    {
        var entries = ClaudeConversation.Read(["""{"type":"user","message":{"role":"user","content":[{"type":"text","text":"Continue."}]}}"""]);

        await Assert.That(entries.Single()).IsEqualTo(entries.Single() with { Kind = ConversationEntryKind.Prompt, Text = "Continue." });
    }

    [Test]
    public async Task FailedTurn_EndsWithItsErrors()
    {
        var entries = ClaudeConversation.Read(["""{"type":"result","subtype":"error_during_execution","is_error":true,"errors":["boom","bang"]}"""]);

        await Assert.That(entries.Single()).IsEqualTo(entries.Single() with { Kind = ConversationEntryKind.TurnEnd, Text = "boom; bang", IsError = true });
    }

    [Test]
    public async Task NotJson_IsSkipped() =>
        await Assert.That(ClaudeConversation.Read(["not json", ""])).IsEmpty();
}
