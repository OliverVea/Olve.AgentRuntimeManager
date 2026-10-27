using Olve.AgentRuntimeManager.Supervisor.Protocol;

namespace Olve.AgentRuntimeManager.Tools.Tools;

/// <summary>
/// The session's skills (M10b), as <c>arm_skill_load</c> shows and finds them: each one's
/// description, read from its <c>SKILL.md</c> once when the server starts (the tool's description
/// lists them, so the agent sees them with its tool list), and the folder the tool reads at call time.
/// </summary>
internal sealed class SessionSkills
{
    public static readonly SessionSkills None = new([]);

    private readonly IReadOnlyList<ToolsSkill> _skills;

    public SessionSkills(IReadOnlyList<ToolsSkill> skills)
    {
        _skills = skills;
        Listing = skills.Count == 0
            ? "This session has no skills."
            : $"This session's skills:\n{string.Join('\n', skills.Select(skill => $"- {skill.Name}: {Describe(skill)}"))}";
    }

    /// <summary>
    /// The end of the tool's description: <c>- &lt;name&gt;: &lt;description&gt;</c> per skill (or why its
    /// <c>SKILL.md</c> couldn't be read), or that the session has none.
    /// </summary>
    public string Listing { get; }

    /// <summary>The names, for an error naming them; "none" without any.</summary>
    public string Names => _skills.Count == 0 ? "none" : string.Join(", ", _skills.Select(skill => skill.Name));

    public ToolsSkill? Find(string name) => _skills.FirstOrDefault(skill => skill.Name == name);

    /// <summary>The skill's <c>SKILL.md</c>, parsed; null with what's wrong in <paramref name="error"/> (no file, unreadable, or the parser's).</summary>
    public static SkillFile? Read(ToolsSkill skill, out string? error)
    {
        var path = Path.Combine(skill.Folder, SkillFile.FileName);
        if (!File.Exists(path))
        {
            error = $"no {SkillFile.FileName} in {skill.Folder}";
            return null;
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            error = exception.Message;
            return null;
        }

        return SkillFile.Parse(text, out error);
    }

    private static string Describe(ToolsSkill skill) =>
        Read(skill, out var error) is { } file
            ? file.Description!.ReplaceLineEndings(" ")
            : $"(its {SkillFile.FileName} couldn't be read: {error})";
}
