using System.Text.Json.Nodes;
using Olve.AgentRuntimeManager.Sessions.Providers.Claude;

namespace Olve.AgentRuntimeManager.UnitTests.Sessions.Claude;

/// <summary>Parsing recorded Claude Code output (Stub/*.jsonl, captured from Claude Code 2.1.283).</summary>
public class ClaudeStreamJsonTests
{
    private static readonly string Recordings = Path.Combine(AppContext.BaseDirectory, "Sessions", "Claude", "Stub");

    [Test]
    public async Task SuccessfulTurn_IsReadFromItsResultEvent()
    {
        var results = (await File.ReadAllLinesAsync(Path.Combine(Recordings, "success.jsonl")))
            .Select(ClaudeStreamJson.ParseResult).OfType<ClaudeResult>().ToList();

        await Assert.That(results).HasSingleItem();
        await Assert.That(results[0]).IsEquivalentTo(new ClaudeResult("success", false, "READY\n\nNO-CANARY", [], "completed"));
    }

    [Test]
    public async Task FailedTurn_CarriesItsErrors()
    {
        var result = (await File.ReadAllLinesAsync(Path.Combine(Recordings, "error.jsonl")))
            .Select(ClaudeStreamJson.ParseResult).OfType<ClaudeResult>().Single();

        await Assert.That(result.Subtype).IsEqualTo("error_during_execution");
        await Assert.That(result.IsError).IsTrue();
        await Assert.That(result.Text).IsNull();
        await Assert.That(result.Errors).HasSingleItem();
    }

    [Test]
    [Arguments("not json")]
    [Arguments("""{"type":"assistant"}""")]
    [Arguments("[1,2]")]
    public async Task OtherLines_AreNotResults(string line) =>
        await Assert.That(ClaudeStreamJson.ParseResult(line)).IsNull();

    [Test]
    public async Task UserMessage_IsOneLineOfTheInputProtocol()
    {
        var line = ClaudeStreamJson.UserMessage("fix \"it\"\nplease");

        await Assert.That(line).DoesNotContain("\n");
        var message = JsonNode.Parse(line)!;
        await Assert.That(message["type"]!.GetValue<string>()).IsEqualTo("user");
        await Assert.That(message["message"]!["content"]!.GetValue<string>()).IsEqualTo("fix \"it\"\nplease");
    }

    [Test]
    public async Task UserMessage_CarriesItsUuid()
    {
        var id = Guid.NewGuid();

        var message = JsonNode.Parse(ClaudeStreamJson.UserMessage("hi", id))!;

        await Assert.That(message["uuid"]!.GetValue<string>()).IsEqualTo(id.ToString());
    }

    [Test]
    public async Task TwoMessages_AreTwoTurns_EachReplayedWithItsUuid()
    {
        var events = (await File.ReadAllLinesAsync(Path.Combine(Recordings, "two-messages.jsonl"))).Select(ClaudeStreamJson.Parse).OfType<JsonObject>().ToList();

        var replays = events.Select(e => ClaudeStreamJson.IsReplay(e, out var uuid) ? uuid : null).OfType<Guid>().ToList();
        var results = events.Select(ClaudeStreamJson.Result).OfType<ClaudeResult>().ToList();

        // The placeholders the stub swaps for the uuids it was given.
        await Assert.That(replays).IsEquivalentTo([Guid.Parse("11111111-1111-4111-8111-111111111111"), Guid.Parse("22222222-2222-4222-8222-222222222222")]);
        await Assert.That(results.Select(r => r.Text ?? "").ToList()).IsEquivalentTo(["ONE", "TWO"]);
    }

    [Test]
    public async Task QueuedTurnCount_DoesNotSayMoreTurnsAreComing()
    {
        // Recorded with the second message already on stdin before the first turn began.
        var results = (await File.ReadAllLinesAsync(Path.Combine(Recordings, "two-messages.jsonl")))
            .Select(ClaudeStreamJson.Parse).OfType<JsonObject>().Where(e => ClaudeStreamJson.Result(e) is not null).ToList();

        await Assert.That(results.Select(r => r["queued_turn_count"]!.GetValue<int>()).ToList()).IsEquivalentTo([0, 0]);
    }

    [Test]
    public async Task TurnSucceeded_LooksAtTheLastTurn()
    {
        var lines = await File.ReadAllLinesAsync(Path.Combine(Recordings, "two-messages.jsonl"));

        await Assert.That(ClaudeStreamJson.TurnSucceeded(lines)).IsTrue();
        // Cut off after the second message was taken up: its turn isn't done.
        await Assert.That(ClaudeStreamJson.TurnSucceeded(lines[..8])).IsFalse();
    }
}
