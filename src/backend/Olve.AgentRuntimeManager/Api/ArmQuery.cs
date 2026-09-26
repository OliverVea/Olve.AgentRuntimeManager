using System.Diagnostics.CodeAnalysis;

namespace Olve.AgentRuntimeManager.Api;

/// <summary>Query-string helpers the generated binding uses.</summary>
public static class ArmQuery
{
    /// <summary>
    /// An <c>explode: false</c> list (<c>?event=a,b</c>): comma-separated, entries trimmed, empty
    /// entries dropped. Absent stays null.
    /// </summary>
    [return: NotNullIfNotNull(nameof(value))]
    public static IReadOnlyList<string>? List(string? value) =>
        value?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
