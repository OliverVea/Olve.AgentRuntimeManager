namespace Olve.AgentRuntimeManager.Sessions.Providers;

/// <summary>A model a provider offers, as configured (<c>Providers:&lt;provider&gt;:Models:&lt;n&gt;</c>).</summary>
public sealed class ModelOptions
{
    /// <summary>What a session gives as <c>model</c>; passed to the provider as is.</summary>
    public string Id { get; set; } = "";

    /// <summary>Its name for people, e.g. <c>Opus</c>. Empty: the id.</summary>
    public string? DisplayName { get; set; }

    public static ModelOptions Of(string id, string displayName) => new() { Id = id, DisplayName = displayName };

    /// <summary>
    /// The configured models, else <paramref name="defaults"/>. The defaults aren't seeded into the
    /// options: the binder would merge a configured list into them instead of replacing them.
    /// </summary>
    public static IReadOnlyList<ModelOptions> OrDefaults(List<ModelOptions>? configured, IReadOnlyList<ModelOptions> defaults) =>
        configured is { Count: > 0 } ? configured : defaults;

    /// <summary>Whether every configured model has an id, and no id is there twice (checked at startup).</summary>
    public static bool AreValid(List<ModelOptions>? configured) =>
        configured is null
        || (configured.All(m => !string.IsNullOrWhiteSpace(m.Id))
            && configured.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() == configured.Count);

    public static string Invalid(string section) => $"{section}:Models: every model needs an Id, and each Id may appear only once.";
}
