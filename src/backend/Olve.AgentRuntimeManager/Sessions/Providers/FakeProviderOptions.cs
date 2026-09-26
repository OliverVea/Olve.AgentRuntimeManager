namespace Olve.AgentRuntimeManager.Sessions.Providers;

/// <summary>Settings of the <see cref="FakeProvider"/> (configuration section <c>Providers:Fake</c>).</summary>
public sealed class FakeProviderOptions
{
    public const string Section = "Providers:Fake";

    /// <summary>How long a fake agent runs unless its prompt says otherwise (<c>fake:sleep=…</c>).</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.FromSeconds(2);
}
