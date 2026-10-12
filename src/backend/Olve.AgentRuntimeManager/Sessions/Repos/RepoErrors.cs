using Olve.AgentRuntimeManager.Api;

namespace Olve.AgentRuntimeManager.Sessions.Repos;

/// <summary>The problems a new session's <c>repos</c> can have (each a 400, code <c>INVALID_REQUEST</c>).</summary>
public static class RepoErrors
{
    /// <summary>The first problem with <paramref name="repos"/>, if there is one.</summary>
    public static ArmError? Check(IReadOnlyList<Api.CreateSessionRepo>? repos)
    {
        var folders = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < (repos?.Count ?? 0); i++)
        {
            var repo = repos![i];
            if (string.IsNullOrWhiteSpace(repo.Url))
            {
                return Invalid($"'repos[{i}].url' cannot be blank.");
            }

            // git would take a leading '-' for an option.
            if (repo.Url.TrimStart().StartsWith('-'))
            {
                return Invalid($"'repos[{i}].url' cannot start with '-'.");
            }

            if (repo.Ref is { } gitRef && (string.IsNullOrWhiteSpace(gitRef) || gitRef.StartsWith('-') || gitRef.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))))
            {
                return Invalid($"'repos[{i}].ref' must be a branch, tag or commit: not blank, no spaces, not starting with '-'.");
            }

            if (RepoNames.Folder(repo.Url) is not { } folder)
            {
                return Invalid($"'repos[{i}].url' ({repo.Url}) has no repository name to name its folder after.");
            }

            if (folders.TryGetValue(folder, out var other))
            {
                return Invalid($"'repos[{other}]' and 'repos[{i}]' would both be checked out in the folder '{folder}'.");
            }

            folders[folder] = i;
        }

        return null;
    }

    private static ArmError Invalid(string message) => ArmError.Create(ArmErrors.InvalidRequest, message);
}
