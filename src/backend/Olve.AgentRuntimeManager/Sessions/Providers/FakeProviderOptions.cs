namespace Olve.AgentRuntimeManager.Sessions.Providers;

/// <summary>Settings of the <see cref="FakeProvider"/> (configuration section <c>Providers:Fake</c>).</summary>
public sealed class FakeProviderOptions
{
    public const string Section = "Providers:Fake";

    /// <summary>
    /// The models of a server that configures none: <c>fake</c>, and Claude's, so a client's Claude
    /// default (the CLI's <c>sonnet</c>) works on the fake provider too.
    /// </summary>
    public static readonly IReadOnlyList<ModelOptions> DefaultModels =
        [ModelOptions.Of("fake", "Fake"), ModelOptions.Of("opus", "Opus"), ModelOptions.Of("sonnet", "Sonnet"), ModelOptions.Of("haiku", "Haiku")];

    /// <summary>Its name for people. Empty: <c>fake</c>.</summary>
    public string? DisplayName { get; set; } = "Fake";

    /// <summary>The models sessions may name (it ignores which). Unset: <see cref="DefaultModels"/>; a configured list replaces them.</summary>
    public List<ModelOptions>? Models { get; set; }

    /// <summary>Whether sessions can use it at all; off in prod, where no session should run without an LLM.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long a fake agent runs unless its prompt says otherwise (<c>fake:sleep=…</c>).</summary>
    public TimeSpan Delay { get; set; } = TimeSpan.FromSeconds(2);
}
