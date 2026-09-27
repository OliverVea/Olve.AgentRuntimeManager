namespace Olve.AgentRuntimeManager.Variables;

/// <summary>Where registered environment variables are kept.</summary>
public interface IEnvStore
{
    /// <summary>Every registered variable, by name.</summary>
    IReadOnlyList<EnvVariableRecord> List();

    /// <summary>Registers a variable, or replaces the one with its name.</summary>
    void Set(EnvVariableRecord variable);

    /// <summary>False if there was none by that name.</summary>
    bool Delete(string name);
}
