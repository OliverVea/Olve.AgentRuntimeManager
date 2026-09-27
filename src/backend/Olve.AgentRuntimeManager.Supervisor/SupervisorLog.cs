using System.Globalization;

namespace Olve.AgentRuntimeManager.Supervisor;

/// <summary>The supervisor's own diagnostics (<c>supervisor.log</c> in the session's folder): it has no stdout.</summary>
internal sealed class SupervisorLog(string path)
{
    private readonly Lock _gate = new();

    public void Write(string message)
    {
        lock (_gate)
        {
            try
            {
                File.AppendAllText(path, $"{DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)} [{Environment.ProcessId}] {message}\n");
            }
            catch (IOException)
            {
                // Diagnostics only.
            }
        }
    }
}
