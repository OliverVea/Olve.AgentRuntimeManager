namespace Olve.AgentRuntimeManager.Sessions.Supervision;

/// <summary>The supervisor stopped answering, and left no exit of its agent behind.</summary>
public sealed class SupervisorLostException(string message) : Exception(message);
