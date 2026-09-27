using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.Variables;

/// <summary>Domain → contract.</summary>
public static class EnvMapping
{
    public static EnvVariable ToDto(this EnvVariableRecord variable) => new()
    {
        Name = variable.Name,
        Value = variable.Value,
        Default = variable.IsDefault,
        UpdatedAt = variable.UpdatedAt,
    };
}
