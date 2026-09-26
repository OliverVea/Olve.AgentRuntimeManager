using Olve.Utilities.AsyncOnStartup;

namespace Olve.AgentRuntimeManager.Sessions;

/// <summary>Once the server is up: finds providers that can't run agents at all (e.g. Claude Code not logged in).</summary>
public sealed class ProviderCheck(SessionManager sessions) : IAsyncOnStartup
{
    public int Priority => 0;

    public Task OnStartupAsync(CancellationToken cancellationToken) => sessions.CheckProvidersAsync(cancellationToken);
}
