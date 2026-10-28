using System.Runtime.Versioning;
using Olve.AgentRuntimeManager.Tools.Calls;

namespace Olve.AgentRuntimeManager.Tools.Tools;

/// <summary>
/// <c>arm_skill_load</c> (M10b): one of the session's skills by name, its <c>SKILL.md</c>'s
/// instructions (what follows the frontmatter) and where its files are, so the agent reads them
/// with <c>arm_file_read</c> and runs its scripts with <c>arm_bash</c>. Read at call time, not
/// cached: the session's copy is its own and doesn't change under it.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal static class SkillLoadTool
{
    public static ToolResult Run(ToolArguments arguments, ToolContext context)
    {
        var name = arguments.RequiredString("name");
        if (arguments.Failure is { } failure)
        {
            return failure;
        }

        if (context.Skills.Find(name) is not { } skill)
        {
            return ToolResult.Error($"There's no skill '{name}' in this session. Its skills: {context.Skills.Names}.");
        }

        if (SessionSkills.Read(skill, out var error) is not { } file)
        {
            return ToolResult.Error($"Skill '{name}' can't be loaded: {error}");
        }

        return ToolResult.Ok($"Skill '{name}' (its files are in {skill.Folder}: read them with arm_file_read, run its scripts with arm_bash):\n\n{file.Body}");
    }
}
