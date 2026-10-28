using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Olve.AgentRuntimeManager.Tools;
using Olve.AgentRuntimeManager.Tools.Calls;
using Olve.AgentRuntimeManager.Tools.Journal;
using Olve.AgentRuntimeManager.Tools.Protocol;
using Olve.AgentRuntimeManager.Tools.Server;
using Olve.AgentRuntimeManager.Tools.Tools;

namespace Olve.AgentRuntimeManager.UnitTests.Tools;

/// <summary>
/// The session's skills in the tools server (M10b): <c>--skill name=folder</c>, the listing at the
/// end of <c>arm_skill_load</c>'s description, and the tool loading one (through the dispatcher).
/// </summary>
[UnsupportedOSPlatform("windows")]
public class SkillLoadToolTests
{
    private const string Pdf = "---\nname: pdf\ndescription: Work with PDFs.\n---\n\n# PDF\n\nRun scripts/fill.py.\n";

    private readonly ToolsTestFolder _folder = new();
    private readonly List<CallJournal> _journals = [];

    [After(Test)]
    public void Cleanup()
    {
        _journals.ForEach(journal => journal.Dispose());
        _folder.Dispose();
    }

    private static string[] Launch(params string[] more) => ["--workplace", "/w", "--journal", "/w/tools.jsonl", .. more];

    [Test]
    public async Task Parse_Skills_InOrder_FoldersMadeAbsolute()
    {
        var options = ToolsOptions.Parse(Launch("--skill", "pdf=/skills/pdf", "--tool", ToolCatalog.SkillLoad, "--skill", "docs=skills/docs"), out var error);

        await Assert.That(error).IsNull();
        await Assert.That(options!.Skills).IsEquivalentTo([new ToolsSkill("pdf", "/skills/pdf"), new ToolsSkill("docs", Path.GetFullPath("skills/docs"))]);
        await Assert.That(options.Tools).IsEquivalentTo([ToolCatalog.SkillLoad]);
    }

    [Test]
    public async Task Parse_NoSkills_IsAnEmptyList()
    {
        var options = ToolsOptions.Parse(Launch(), out _);

        await Assert.That(options!.Skills).IsEmpty();
    }

    [Test]
    [Arguments("pdf")]
    [Arguments("pdf=")]
    [Arguments("=/skills/pdf")]
    public async Task Parse_ASkillWithoutANameOrFolder_IsAUsageError(string value)
    {
        var options = ToolsOptions.Parse(Launch("--skill", value), out var error);

        await Assert.That(options).IsNull();
        await Assert.That(error).IsEqualTo($"--skill takes <name>=<folder>, not '{value}'.");
    }

    [Test]
    public async Task Parse_ASkillNamedTwice_IsAUsageError()
    {
        var options = ToolsOptions.Parse(Launch("--skill", "pdf=/a", "--skill", "pdf=/b"), out var error);

        await Assert.That(options).IsNull();
        await Assert.That(error).IsEqualTo("the skill 'pdf' is given twice.");
    }

    [Test]
    public async Task List_WithoutSkills_SaysSo_AtTheEndOfTheDescription()
    {
        var list = ToolDefinitions.List([ToolCatalog.SkillLoad], SessionSkills.None);

        var description = list[0]!["description"]!.GetValue<string>();
        await Assert.That(description).StartsWith("Load one of the session's skills: instructions to follow for a kind of task.");
        await Assert.That(description).EndsWith("\n\nThis session has no skills.");
        await Assert.That(list[0]!["inputSchema"]!["required"]![0]!.GetValue<string>()).IsEqualTo("name");
    }

    [Test]
    public async Task List_WithSkills_EndsWithOneLineEach_ReadOnce()
    {
        var pdf = Path.GetDirectoryName(_folder.Write("skills/pdf/SKILL.md", Pdf))!;
        var folded = Path.GetDirectoryName(_folder.Write("skills/folded/SKILL.md", "---\ndescription: >\n  Two\n  lines.\n---\n"))!;
        var broken = Path.GetDirectoryName(_folder.Write("skills/broken/README.md", "no SKILL.md here"))!;
        var invalid = Path.GetDirectoryName(_folder.Write("skills/invalid/SKILL.md", "---\nname: x\n---\n"))!;
        var skills = new SessionSkills([new("pdf", pdf), new("folded", folded), new("broken", broken), new("invalid", invalid)]);
        File.Delete(Path.Combine(pdf, "SKILL.md"));

        var description = ToolDefinitions.List([ToolCatalog.SkillLoad], skills)[0]!["description"]!.GetValue<string>();

        // Read when the server starts (the file's gone since), not at each list.
        await Assert.That(description).EndsWith(
            "This session's skills:\n"
            + "- pdf: Work with PDFs.\n"
            + "- folded: Two lines.\n"
            + $"- broken: (its SKILL.md couldn't be read: no SKILL.md in {broken})\n"
            + "- invalid: (its SKILL.md couldn't be read: SKILL.md's frontmatter has no 'description'.)");
    }

    [Test]
    public async Task List_OtherTools_DoNotListTheSkills()
    {
        var skills = new SessionSkills([new("pdf", "/skills/pdf")]);

        var description = ToolDefinitions.List([ToolCatalog.Bash], skills)[0]!["description"]!.GetValue<string>();

        await Assert.That(description).DoesNotContain("skills");
    }

    [Test]
    public async Task Load_ASkill_IsItsHeaderAndBody()
    {
        var pdf = Path.GetDirectoryName(_folder.Write("skills/pdf/SKILL.md", Pdf))!;
        _folder.Skills = new SessionSkills([new("pdf", pdf)]);

        var result = await Call("""{ "name": "pdf" }""");

        await Assert.That(result!.IsError).IsFalse();
        await Assert.That(result.Text).IsEqualTo($"Skill 'pdf' (its files are in {pdf}: read them with arm_file_read, run its scripts with arm_bash):\n\n# PDF\n\nRun scripts/fill.py.");
    }

    [Test]
    public async Task Load_AnUnknownSkill_IsAnErrorNamingTheSessions()
    {
        _folder.Skills = new SessionSkills([new("pdf", "/skills/pdf"), new("docs", "/skills/docs")]);

        var result = await Call("""{ "name": "nope" }""");

        await Assert.That(result!.IsError).IsTrue();
        await Assert.That(result.Text).IsEqualTo("There's no skill 'nope' in this session. Its skills: pdf, docs.");
    }

    [Test]
    public async Task Load_WithoutSkills_NamesNone()
    {
        var result = await Call("""{ "name": "pdf" }""");

        await Assert.That(result!.IsError).IsTrue();
        await Assert.That(result.Text).IsEqualTo("There's no skill 'pdf' in this session. Its skills: none.");
    }

    [Test]
    public async Task Load_WithoutAName_IsAnError()
    {
        var result = await Call("{}");

        await Assert.That(result!.IsError).IsTrue();
        await Assert.That(result.Text).IsEqualTo("`name` is required.");
    }

    [Test]
    public async Task Load_ASkillWhoseFileIsGone_IsAnError()
    {
        _folder.Skills = new SessionSkills([new("pdf", _folder.PathOf("skills/pdf"))]);

        var result = await Call("""{ "name": "pdf" }""");

        await Assert.That(result!.IsError).IsTrue();
        await Assert.That(result.Text).IsEqualTo($"Skill 'pdf' can't be loaded: no SKILL.md in {_folder.PathOf("skills/pdf")}");
    }

    private Task<ToolResult?> Call(string arguments)
    {
        var journal = new CallJournal(_folder.PathOf("session/tools.jsonl"));
        _journals.Add(journal);
        var dispatcher = new ToolDispatcher(_folder.Context, [ToolCatalog.SkillLoad], journal);
        return dispatcher.CallAsync(ToolCatalog.SkillLoad, JsonNode.Parse(arguments) as JsonObject, null, CancellationToken.None, CancellationToken.None);
    }
}
