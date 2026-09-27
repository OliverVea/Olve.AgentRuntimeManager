namespace Olve.AgentRuntimeManager.Supervisor.Protocol;

/// <summary>
/// The supervisor protocol's version. Frozen and additive: fields and message types may be added,
/// nothing changes meaning, unknown fields are ignored. A supervisor from an older release must
/// stay reachable by every newer ARM (a session can outlive several deploys); a real break needs
/// a new major, and ARM keeps speaking every major that can still be running.
/// </summary>
public static class SupervisorProtocol
{
    public const int Version = 1;
}
