namespace Olve.AgentRuntimeManager.Sessions.Providers;

/// <summary>Why a provider can't run agents right now (<see cref="AgentOutcome.Unavailable"/>).</summary>
public enum ProviderTrouble
{
    /// <summary>Out of usage, e.g. a subscription's session limit.</summary>
    Limited,

    /// <summary>The provider's API failed: an outage, overload or network error.</summary>
    Unreachable,

    /// <summary>The provider's credentials are missing or were rejected.</summary>
    Unauthorized,
}
