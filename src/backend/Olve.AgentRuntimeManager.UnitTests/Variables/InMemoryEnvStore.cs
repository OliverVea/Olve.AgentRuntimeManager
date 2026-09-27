using Olve.AgentRuntimeManager.Variables;

namespace Olve.AgentRuntimeManager.UnitTests.Variables;

/// <summary>An <see cref="IEnvStore"/> in a dictionary.</summary>
public sealed class InMemoryEnvStore : IEnvStore
{
    private readonly Dictionary<string, EnvVariableRecord> _variables = new(StringComparer.Ordinal);

    public IReadOnlyList<EnvVariableRecord> List() => [.. _variables.Values.OrderBy(v => v.Name, StringComparer.Ordinal)];

    public void Set(EnvVariableRecord variable) => _variables[variable.Name] = variable;

    public bool Delete(string name) => _variables.Remove(name);

    public InMemoryEnvStore With(string name, string value, bool isDefault)
    {
        Set(new EnvVariableRecord { Name = name, Value = value, IsDefault = isDefault, UpdatedAt = DateTimeOffset.UnixEpoch });
        return this;
    }
}
