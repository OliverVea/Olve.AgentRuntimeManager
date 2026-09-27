using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.Variables;

/// <summary>The environment-variable error codes (stable once published, docs/STANDARDS.md).</summary>
public static class EnvErrors
{
    public static ArmError InvalidName(string name) =>
        ArmError.Create(ArmErrors.InvalidRequest, $"'{name}' is not a valid environment variable name (letters, digits and '_', not starting with a digit).");

    public static ArmError ReservedName(string name) =>
        ArmError.Create("RESERVED_ENV_NAME", $"'{name}' is set by ARM for its agents and can't be given.");

    public static ArmError NotFound(string name) =>
        ArmError.Create("ENV_NOT_FOUND", $"No environment variable '{name}' is registered.");

    public static ArmError UnknownUseEnv(IReadOnlyList<string> names) =>
        ArmError.Create("ENV_NOT_FOUND", $"No environment variable {string.Join(", ", names.Select(n => $"'{n}'"))} is registered.");

    /// <summary>The problem with a name, if it has one.</summary>
    public static ArmError? Check(string name) =>
        !EnvNames.IsValid(name) ? InvalidName(name)
        : EnvNames.IsReserved(name) ? ReservedName(name)
        : null;
}
