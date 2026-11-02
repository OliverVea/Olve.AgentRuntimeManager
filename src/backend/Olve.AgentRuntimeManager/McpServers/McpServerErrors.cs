using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.McpServers;

/// <summary>The MCP server error codes (stable once published, docs/STANDARDS.md).</summary>
public static class McpServerErrors
{
    public static ArmError InvalidName(string name) =>
        ArmError.Create(ArmErrors.InvalidRequest,
            $"'{name}' is not a valid MCP server name (lowercase letters, digits and '-', starting with a letter, at most {McpServerNames.MaxLength} characters).");

    public static ArmError ReservedName(string name) =>
        ArmError.Create("RESERVED_MCP_SERVER_NAME", $"'{name}' is reserved for ARM ('arm' and names starting with 'arm-').");

    public static ArmError NotFound(string name) =>
        ArmError.Create("MCP_SERVER_NOT_FOUND", $"No MCP server '{name}' is registered.");

    /// <summary>The problem with a name, if it has one.</summary>
    public static ArmError? Check(string name) =>
        !McpServerNames.IsValid(name) ? InvalidName(name)
        : McpServerNames.IsReserved(name) ? ReservedName(name)
        : null;
}
