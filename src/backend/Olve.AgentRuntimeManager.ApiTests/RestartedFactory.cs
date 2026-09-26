namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>An in-process host on the database in <paramref name="folder"/>: two in turn are a server and its restart.</summary>
public sealed class RestartedFactory(string folder) : ApiFactory
{
    protected override IReadOnlyDictionary<string, string> Settings { get; } = new Dictionary<string, string>
    {
        ["Sessions:TotalSlots"] = "1000",
        ["ConnectionStrings:Arm"] = ConnectionString(folder),
    };
}
