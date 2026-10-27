using Microsoft.Extensions.Options;
using Olve.AgentRuntimeManager.Tools.Protocol;

namespace Olve.AgentRuntimeManager.Sessions;

/// <summary>Startup fails on a <c>Tools:Default</c> that names a tool ARM doesn't have, saying which and what it has.</summary>
public sealed class ToolsOptionsValidation : IValidateOptions<ToolsOptions>
{
    public ValidateOptionsResult Validate(string? name, ToolsOptions options) =>
        options.UnknownDefaults() is { Count: > 0 } unknown
            ? ValidateOptionsResult.Fail(
                $"{ToolsOptions.Section}:Default names {string.Join(", ", unknown.Select(n => $"'{n}'"))}, which ARM has no tool for. "
                + $"ARM's tools: {string.Join(", ", ToolCatalog.All.Select(t => t.Name))}.")
            : ValidateOptionsResult.Success;
}
