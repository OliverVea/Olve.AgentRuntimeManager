namespace Olve.AgentRuntimeManager.Tools.Protocol;

/// <summary>
/// Every tool ARM has, in the order clients show them: what <c>GET /api/tools</c> lists and what
/// <c>olve-arm-tools</c> serves (only the ones a session names). The arguments are snake_case, as
/// Claude Code's own tools' are, so the conversation's previews read them alike (<c>command</c>,
/// <c>description</c>, <c>file_path</c>).
/// </summary>
public static class ToolCatalog
{
    /// <summary><c>command</c>, <c>description</c>?, <c>working_dir</c>?, <c>timeout_seconds</c>? (default 120, max 600).</summary>
    public const string Bash = "arm_bash";

    /// <summary><c>file_path</c>, <c>offset</c>?, <c>limit</c>?.</summary>
    public const string FileRead = "arm_file_read";

    /// <summary><c>file_path</c>, <c>old_string</c>, <c>new_string</c>, <c>replace_all</c>?.</summary>
    public const string FileEdit = "arm_file_edit";

    /// <summary><c>file_path</c>, <c>content</c>.</summary>
    public const string FileCreate = "arm_file_create";

    /// <summary><c>file_path</c>, <c>recursive</c>?.</summary>
    public const string FileDelete = "arm_file_delete";

    /// <summary><c>file_path</c> (the source), <c>destination_path</c>, <c>overwrite</c>?.</summary>
    public const string FileCopy = "arm_file_copy";

    /// <summary><c>file_path</c> (the source), <c>destination_path</c>, <c>overwrite</c>?.</summary>
    public const string FileMove = "arm_file_move";

    public static IReadOnlyList<ArmTool> All { get; } =
    [
        new(Bash, "Run a shell command in a fresh bash, in the workplace or a given folder."),
        new(FileRead, "Read a text file, as numbered lines."),
        new(FileEdit, "Replace an exact string in a file."),
        new(FileCreate, "Create a new file; refuses one that exists."),
        new(FileDelete, "Delete a file, or a folder with recursive."),
        new(FileCopy, "Copy a file or folder; refuses an existing target (overwrite replaces files only, never folders)."),
        new(FileMove, "Move or rename a file or folder; refuses an existing target (overwrite replaces files only, never folders)."),
    ];

    public static ArmTool? Find(string name) => All.FirstOrDefault(tool => tool.Name == name);
}
