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
        await Assert.That(results[0]).IsEquivalentTo(new ClaudeResult("success", false, "READY\n\nNO-CANARY", []));
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
}
