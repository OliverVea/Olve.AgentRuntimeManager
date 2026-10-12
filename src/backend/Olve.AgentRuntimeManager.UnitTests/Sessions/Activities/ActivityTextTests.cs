using Olve.AgentRuntimeManager.Sessions.Activities;

namespace Olve.AgentRuntimeManager.UnitTests.Sessions.Activities;

/// <summary>Text made into one line of activity.</summary>
public class ActivityTextTests
{
    [Test]
    [Arguments("Looking at the build.", "Looking at the build.")]
    [Arguments("\n\n  First line  \nsecond line", "First line")]
    [Arguments("## Plan\n\n1. Read", "Plan")]
    [Arguments("- **Bold** and `code`", "Bold and code")]
    [Arguments("> - quoted item", "quoted item")]
    [Arguments("3) third", "third")]
    [Arguments("```bash\nnpx mise run ci\n```", "npx mise run ci")]
    [Arguments("See [the docs](https://example.com/d) now", "See the docs (https://example.com/d) now")]
    [Arguments("[https://example.com](https://example.com)", "https://example.com")]
    [Arguments("a\tb \r", "a b")]
    [Arguments("*.cs files and 2 * 3", "*.cs files and 2 * 3")]
    public async Task FirstLine_WithoutMarkdownNoise(string text, string line)
    {
        await Assert.That(ActivityText.FirstLine(text)).IsEqualTo(line);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments(" \n\t\n")]
    [Arguments("```\n```")]
    [Arguments("#   ")]
    public async Task FirstLine_OfNothing_IsNull(string? text)
    {
        await Assert.That(ActivityText.FirstLine(text)).IsNull();
    }

    [Test]
    [Arguments("remote: no\nfatal: denied\nhint: ask", "fatal: denied")]
    [Arguments("Exit code 1\n\nsomething went wrong\n", "something went wrong")]
    [Arguments("\u001b[31mError:\u001b[0m boom\u0007", "Error: boom")]
    [Arguments("only\tline", "only line")]
    public async Task ErrorLine_SaysWhatWentWrong(string text, string line)
    {
        await Assert.That(ActivityText.ErrorLine(text)).IsEqualTo(line);
    }

    [Test]
    public async Task LongText_IsCut_AndMarked()
    {
        var line = ActivityText.FirstLine(new string('a', 500) + "\nmore")!;

        await Assert.That(line.Length).IsEqualTo(ActivityText.MaxLength);
        await Assert.That(line).IsEqualTo(new string('a', ActivityText.MaxLength - 1) + "…");
        await Assert.That(ActivityText.Bound(new string('b', ActivityText.MaxLength))).IsEqualTo(new string('b', ActivityText.MaxLength));
    }

    [Test]
    public async Task Cut_IsBetweenCharacters_NeverInsideOne()
    {
        // 198 letters, then emoji of two UTF-16 units each (and one of several, joined): the cut can't split them.
        var text = new string('a', 198) + "😀😀" + "👩‍👩‍👧" + new string('z', 50);

        var line = ActivityText.Bound(text);

        await Assert.That(line).IsEqualTo(new string('a', 198) + "…");
        await Assert.That(ActivityText.Bound(new string('a', 197) + "😀😀")).IsEqualTo(new string('a', 197) + "😀…");
    }
}
