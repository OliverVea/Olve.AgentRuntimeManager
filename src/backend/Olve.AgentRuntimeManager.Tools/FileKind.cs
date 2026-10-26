namespace Olve.AgentRuntimeManager.Tools;

/// <summary>What a path is (<see cref="Native.Kind"/>): the file tools read only regular files.</summary>
internal enum FileKind
{
    Regular,
    Directory,
    Fifo,
    Socket,
    Device,
    Other,
}
