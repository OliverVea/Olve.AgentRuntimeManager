using System.Text.Json.Nodes;
using Olve.AgentRuntimeManager.Tools.Protocol;
using Olve.AgentRuntimeManager.Tools.Tools;

namespace Olve.AgentRuntimeManager.Tools.Server;

/// <summary>
/// How <c>tools/list</c> shows each tool to the agent: the catalog's one line, then what the agent
/// needs to use it well, and a JSON Schema of its arguments (snake_case, as in <see cref="ToolCatalog"/>).
/// </summary>
internal static class ToolDefinitions
{
    private const string FilePath = """
        "file_path": { "type": "string", "description": "The file: relative to the workplace, or an absolute path." }
        """;

    private static readonly Dictionary<string, (string Details, string Schema)> Definitions = new()
    {
        [ToolCatalog.Bash] = (
            """
            Each call is a fresh `bash -c`: nothing carries over between calls (no `cd`, variables or functions), so chain steps with `&&` in one command or use `working_dir`. It runs in the workplace, or in `working_dir`.
            stdin is /dev/null: nothing interactive. stdout and stderr come back together, with the exit code. Long output keeps its head and tail, with a note of how much was cut between.
            `timeout_seconds` is 120 by default, at most 600; at the timeout the command and everything it started are stopped. Anything still running when the command ends (started with `&`, say) is stopped too: start nothing meant to outlive the call.
            For files, prefer arm_file_read, arm_file_edit and arm_file_create over cat, sed or echo.
            """,
            """
            {
              "type": "object",
              "properties": {
                "command": { "type": "string", "description": "The command, run with bash -c." },
                "description": { "type": "string", "description": "What the command does, in a few words, for whoever follows the session." },
                "working_dir": { "type": "string", "description": "The folder to run it in: relative to the workplace, or an absolute path. The workplace by default." },
                "timeout_seconds": { "type": "integer", "description": "Seconds before the command is stopped: 120 by default, at most 600." }
              },
              "required": ["command"]
            }
            """),
        [ToolCatalog.FileRead] = (
            """
            Lines are numbered from 1, as `cat -n` shows them (the number and a tab come before each line; they aren't part of the file). From line `offset`, at most `limit` lines (2000 by default). Very long lines are cut, and so is a read past the output cap: it says which offset to read on from.
            Folders and binary files are refused: use arm_bash for those (ls, file, xxd).
            """,
            $$"""
            {
              "type": "object",
              "properties": {
                {{FilePath}},
                "offset": { "type": "integer", "description": "The first line to read, from 1. 1 by default." },
                "limit": { "type": "integer", "description": "How many lines to read at most. 2000 by default." }
              },
              "required": ["file_path"]
            }
            """),
        [ToolCatalog.FileEdit] = (
            """
            `old_string` must match the file exactly: whitespace, indentation and line endings too (leave out the line numbers arm_file_read shows). It must be in the file once, unless `replace_all` is set, which replaces every occurrence. Give enough of the text around it to make it unique.
            """,
            $$"""
            {
              "type": "object",
              "properties": {
                {{FilePath}},
                "old_string": { "type": "string", "description": "The exact text to replace." },
                "new_string": { "type": "string", "description": "The text to put in its place (may be empty, to delete it)." },
                "replace_all": { "type": "boolean", "description": "Replace every occurrence, not only a unique one. False by default." }
              },
              "required": ["file_path", "old_string", "new_string"]
            }
            """),
        [ToolCatalog.FileCreate] = (
            """
            Writes `content` (UTF-8) to a new file, making its folders as needed. A file that exists is refused: change it with arm_file_edit.
            """,
            $$"""
            {
              "type": "object",
              "properties": {
                {{FilePath}},
                "content": { "type": "string", "description": "The whole file's text." }
              },
              "required": ["file_path", "content"]
            }
            """),
        [ToolCatalog.FileDelete] = (
            """
            A file, or a folder and everything in it only with `recursive`. A link is deleted itself, not what it points to.
            """,
            $$"""
            {
              "type": "object",
              "properties": {
                "file_path": { "type": "string", "description": "The file or folder: relative to the workplace, or an absolute path." },
                "recursive": { "type": "boolean", "description": "Needed to delete a folder (and everything in it). False by default." }
              },
              "required": ["file_path"]
            }
            """),
        [ToolCatalog.FileCopy] = (
            """
            A file, or a folder with everything in it. `destination_path` is the copy's own path, not a folder to put it in. A file that exists there is refused unless `overwrite`, which replaces it (with a file only). A folder that exists there is always refused: delete it first (arm_file_delete with `recursive`). Links are copied as links.
            """,
            TransferSchema("copy")),
        [ToolCatalog.FileMove] = (
            """
            A file, or a folder with everything in it. `destination_path` is the new path itself, not a folder to put it in. A file that exists there is refused unless `overwrite`, which replaces it (with a file only). A folder that exists there is always refused: delete it first (arm_file_delete with `recursive`). A link moves as a link.
            """,
            TransferSchema("move")),
        [ToolCatalog.SkillLoad] = (
            """
            A skill is instructions for a kind of task, with files of its own (references, scripts, templates). Before a task one of the skills below covers, load it by `name` and follow what comes back: the skill's instructions, and the folder its files are in (read them with arm_file_read, run its scripts with arm_bash). Load it again whenever needed; the text is the same every time.
            """,
            """
            {
              "type": "object",
              "properties": {
                "name": { "type": "string", "description": "The skill's name, one of those listed." }
              },
              "required": ["name"]
            }
            """),
    };

    /// <summary>
    /// The <c>tools/list</c> entries of <paramref name="tools"/>, in the catalog's order;
    /// <c>arm_skill_load</c>'s description ends with the session's <paramref name="skills"/>.
    /// </summary>
    public static JsonArray List(IReadOnlyCollection<string> tools, SessionSkills skills)
    {
        var list = new JsonArray();
        foreach (var tool in ToolCatalog.All.Where(tool => tools.Contains(tool.Name)))
        {
            var (details, schema) = Definitions[tool.Name];
            var description = $"{tool.Description}\n\n{details.Trim()}";
            if (tool.Name == ToolCatalog.SkillLoad)
            {
                description = $"{description}\n\n{skills.Listing}";
            }

            list.Add((JsonNode)new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = description,
                ["inputSchema"] = JsonNode.Parse(schema),
            });
        }

        return list;
    }

    private static string TransferSchema(string verb) => $$"""
        {
          "type": "object",
          "properties": {
            "file_path": { "type": "string", "description": "What to {{verb}}, a file or a folder: relative to the workplace, or an absolute path." },
            "destination_path": { "type": "string", "description": "Its new path (not a folder to put it in): relative to the workplace, or an absolute path." },
            "overwrite": { "type": "boolean", "description": "Replace a file that exists at the destination (never a folder). False by default." }
          },
          "required": ["file_path", "destination_path"]
        }
        """;
}
