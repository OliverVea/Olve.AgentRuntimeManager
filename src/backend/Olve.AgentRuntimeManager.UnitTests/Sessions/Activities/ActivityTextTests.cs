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
    [Arguments("* starred item", "starred item")]
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

    [Test]
    [Arguments("> 5 seconds left")]
    [Arguments("2026. Plans")]
    [Arguments("**kwargs and **args")]
    [Arguments("handlers[name](args)")]
    [Arguments("a ** b ** c")]
    [Arguments("-1 is the answer")]
    public async Task OrdinaryText_IsLeftAlone(string text)
    {
        await Assert.That(ActivityText.FirstLine(text)).IsEqualTo(text);
    }

    [Test]
    [Arguments("0 errors, 3 warnings\nfatal: boom", "fatal: boom")]
    [Arguments("Build: 0 errors\nno errors found\nsomething else", "something else")]
    [Arguments("10 errors\nlast", "10 errors")]
    public async Task ErrorLine_DoesNotTakeAZeroCountForTheCause(string text, string line)
    {
        await Assert.That(ActivityText.ErrorLine(text)).IsEqualTo(line);
    }

    [Test]
    public async Task HugeText_IsShapedFromItsStart_Quickly()
    {
        // A long run of '[' made the link pattern quadratic (3 million took minutes); a huge line is
        // cut before it's shaped.
        var brackets = new string('[', 1_000_000) + "](x";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var line = ActivityText.FirstLine(brackets)!;
        var error = ActivityText.ErrorLine(string.Concat(Enumerable.Repeat("x\n", 2_000_000)) + "fatal: at the end")!;
        clock.Stop();

        await Assert.That(clock.Elapsed).IsLessThan(TimeSpan.FromSeconds(2));
        await Assert.That(line).IsEqualTo(new string('[', ActivityText.MaxLength - 1) + "…");
        await Assert.That(error).IsEqualTo("fatal: at the end");
    }

    [Test]
    [Arguments("abc\u202Edef", "abcdef")]
    [Arguments("\u2066x\u2069 \u200By\u200C\u200E\u200F\uFEFF", "x y")]
    [Arguments("a\u202A\u202B\u202C\u202D\u2067\u2068b", "ab")]
    [Arguments("hidden\U000E0041\U000E0042tags", "hiddentags")]
    [Arguments("a\u200Db", "ab")]
    [Arguments("family 👩‍👩‍👧 and ❤️‍🔥 stay", "family 👩‍👩‍👧 and ❤️‍🔥 stay")]
    public async Task FormatCharacters_ThatReorderOrHideText_AreDropped(string text, string line)
    {
        await Assert.That(ActivityText.FirstLine(text)).IsEqualTo(line);
        await Assert.That(ActivityText.ErrorLine(text)).IsEqualTo(line);
    }
}
