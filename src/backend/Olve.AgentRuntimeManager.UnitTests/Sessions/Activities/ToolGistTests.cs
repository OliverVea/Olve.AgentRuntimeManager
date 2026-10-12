using System.Text.Json;
using Olve.AgentRuntimeManager.Sessions.Activities;

namespace Olve.AgentRuntimeManager.UnitTests.Sessions.Activities;

/// <summary>
/// A tool call in short, by the web UI's rules: both are tested against the same cases
/// (<c>src/frontend/src/sessions/tool-gist.cases.json</c>), so every client shows the same line.
/// </summary>
public class ToolGistTests
{
    public static IEnumerable<Func<(string Input, string Gist)>> Cases()
    {
        using var cases = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Sessions", "Activities", "tool-gist.cases.json")));
        foreach (var c in cases.RootElement.EnumerateArray())
        {
            var input = c.TryGetProperty("input", out var i) ? i.GetRawText() : "";
            var gist = c.GetProperty("gist").GetString()!;
            yield return () => (input, gist);
        }
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task SameAsTheWebUi((string Input, string Gist) c)
    {
        JsonElement? input = c.Input.Length > 0 ? JsonDocument.Parse(c.Input).RootElement : null;

        await Assert.That(ToolGist.Of(input)).IsEqualTo(c.Gist);
    }
}
