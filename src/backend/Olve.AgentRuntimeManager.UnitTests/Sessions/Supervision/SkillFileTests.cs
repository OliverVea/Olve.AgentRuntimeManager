using Olve.AgentRuntimeManager.Supervisor.Protocol;

namespace Olve.AgentRuntimeManager.UnitTests.Sessions.Supervision;

/// <summary>Reading a skill's <c>SKILL.md</c> (M10b): the frontmatter ARM needs, and the instructions after it.</summary>
public class SkillFileTests
{
    [Test]
    public async Task Parse_PlainValues_NameDescriptionAndBody()
    {
        var file = SkillFile.Parse("---\nname: pdf\ndescription: Work with PDFs: read, fill, merge.\n---\n\n# PDF\n\nDo the thing.\n", out var error);

        await Assert.That(error).IsNull();
        await Assert.That(file!.Name).IsEqualTo("pdf");
        await Assert.That(file.Description).IsEqualTo("Work with PDFs: read, fill, merge.");
        await Assert.That(file.Body).IsEqualTo("# PDF\n\nDo the thing.");
    }

    [Test]
    [Arguments("\"Quoted: with a \\\"word\\\" in it\"", "Quoted: with a \"word\" in it")]
    [Arguments("'Single quoted'", "Single quoted")]
    [Arguments("\"Line one.\\nLine two.\"", "Line one.\nLine two.")]
    [Arguments("\"A back\\\\slash and \\x\"", "A back\\slash and \\x")]
    [Arguments("'It''s quoted'", "It's quoted")]
    public async Task Parse_QuotedValues_LoseTheirQuotes(string value, string expected)
    {
        var file = SkillFile.Parse($"---\nname: x\ndescription: {value}\n---\nbody", out _);

        await Assert.That(file!.Description).IsEqualTo(expected);
    }

    [Test]
    [Arguments("\uFEFF---\nname: x\ndescription: d\n---\nbody")]
    [Arguments("\n  \n---\nname: x\ndescription: d\n---\nbody")]
    public async Task Parse_ABomOrBlankLinesBeforeTheFrontmatter_AreTakenInStride(string text)
    {
        var file = SkillFile.Parse(text, out var error);

        await Assert.That(error).IsNull();
        await Assert.That(file!.Description).IsEqualTo("d");
        await Assert.That(file.Body).IsEqualTo("body");
    }

    [Test]
    [Arguments(">")]
    [Arguments("|")]
    [Arguments(">-")]
    [Arguments("|-")]
    public async Task Parse_ABlockScalarDescription_IsFoldedToOneLine(string indicator)
    {
        var file = SkillFile.Parse($"---\nname: x\ndescription: {indicator}\n  The first line,\n\n  and the second.\nlicense: MIT\n---\nbody", out var error);

        await Assert.That(error).IsNull();
        await Assert.That(file!.Description).IsEqualTo("The first line, and the second.");
        await Assert.That(file.Frontmatter["license"]).IsEqualTo("MIT");
    }

    [Test]
    public async Task Parse_WindowsLineEndings_CommentsAndNestedStructures_AreTakenInStride()
    {
        var file = SkillFile.Parse("---\r\n# a comment\r\nname: x\r\nmetadata:\r\n  author: someone\r\ndescription: Fine.\r\n---\r\nline 1\r\nline 2\r\n", out var error);

        await Assert.That(error).IsNull();
        await Assert.That(file!.Description).IsEqualTo("Fine.");
        await Assert.That(file.Frontmatter.ContainsKey("author")).IsFalse();
        await Assert.That(file.Body).IsEqualTo("line 1\nline 2");
    }

    [Test]
    public async Task Parse_NoName_IsFine_TheRegistryNamesIt()
    {
        var file = SkillFile.Parse("---\ndescription: Fine.\n---\n", out var error);

        await Assert.That(error).IsNull();
        await Assert.That(file!.Name).IsNull();
        await Assert.That(file.Body).IsEqualTo("");
    }

    [Test]
    public async Task Parse_NoFrontmatter_IsAnError()
    {
        var file = SkillFile.Parse("# Just markdown\n\nNo frontmatter here.\n", out var error);

        await Assert.That(file).IsNull();
        await Assert.That(error).IsEqualTo("SKILL.md doesn't start with a YAML frontmatter (a '---' line).");
    }

    [Test]
    public async Task Parse_AnUnclosedFrontmatter_IsAnError()
    {
        var file = SkillFile.Parse("---\nname: x\ndescription: never closed\n", out var error);

        await Assert.That(file).IsNull();
        await Assert.That(error).IsEqualTo("SKILL.md's frontmatter never closes (no second '---' line).");
    }

    [Test]
    [Arguments("---\nname: x\n---\nbody")]
    [Arguments("---\nname: x\ndescription:\n---\nbody")]
    [Arguments("---\nname: x\ndescription: \"\"\n---\nbody")]
    public async Task Parse_NoDescription_IsAnError(string text)
    {
        var file = SkillFile.Parse(text, out var error);

        await Assert.That(file).IsNull();
        await Assert.That(error).IsEqualTo("SKILL.md's frontmatter has no 'description'.");
    }

    [Test]
    public async Task Parse_Empty_IsAnError()
    {
        await Assert.That(SkillFile.Parse("", out var error)).IsNull();
        await Assert.That(error).Contains("doesn't start with a YAML frontmatter");
    }
}
