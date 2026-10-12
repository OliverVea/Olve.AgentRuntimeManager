using System.Security.Cryptography;
using System.Text;

namespace Olve.AgentRuntimeManager.Sessions.Repos;

/// <summary>Where a session's repositories go (M5e): their folders, branches and shared copies, all from the URL and the session.</summary>
public static class RepoNames
{
    /// <summary>
    /// The folder a repository is checked out in: the URL's last path segment without <c>.git</c>
    /// (<c>git@github.com:o/r.git</c> → <c>r</c>). Null if that leaves no usable folder name.
    /// </summary>
    public static string? Folder(string url)
    {
        var trimmed = url.Trim().TrimEnd('/');
        var name = trimmed[(trimmed.LastIndexOfAny(['/', ':', '\\']) + 1)..];
        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return name is "" or "." or ".." || name.Any(char.IsControl) ? null : name;
    }

    /// <summary>The session's branch in every repository it names: <c>arm/</c> and the first 8 characters of its id.</summary>
    public static string Branch(Guid sessionId) => $"arm/{sessionId.ToString()[..8]}";

    /// <summary>
    /// A repository of the session <paramref name="sessionId"/>, with the folder and branch it is
    /// checked out as. Made once, when the session is created (or, for a session stored before
    /// these were, when the database is brought up to date); stored, checked out and returned as is.
    /// </summary>
    /// <exception cref="InvalidOperationException">The URL names no folder (<see cref="RepoErrors.Check"/> refuses such a session).</exception>
    public static RepoRecord Record(Guid sessionId, string url, string? gitRef = null) =>
        new(url, gitRef, Folder(url) ?? throw new InvalidOperationException($"'{url}' names no repository folder."), Branch(sessionId));

    /// <summary>
    /// The shared copy's folder name: the repository's folder name and a hash of the exact URL, so two
    /// repositories with one name don't meet (and one repository by two URLs gets two copies).
    /// </summary>
    public static string Shared(string url)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url.Trim())))[..12];
        return $"{Folder(url) ?? "repo"}-{hash}.git";
    }
}
