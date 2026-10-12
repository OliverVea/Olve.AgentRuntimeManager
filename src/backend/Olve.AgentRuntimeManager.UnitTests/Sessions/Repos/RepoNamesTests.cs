using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Sessions.Repos;

namespace Olve.AgentRuntimeManager.UnitTests.Sessions.Repos;

/// <summary>Where a session's repositories go (M5e), and which <c>repos</c> a new session can't have.</summary>
public class RepoNamesTests
{
    [Test]
    [Arguments("git@github.com:OliverVea/Olve.AgentRuntimeManager.git", "Olve.AgentRuntimeManager")]
    [Arguments("https://github.com/OliverVea/Olve.Utilities", "Olve.Utilities")]
    [Arguments("https://github.com/OliverVea/Olve.Utilities/", "Olve.Utilities")]
    [Arguments("ssh://git@host:2222/team/app.git", "app")]
    [Arguments("git@host:app.git", "app")]
    [Arguments("/srv/git/tools.GIT", "tools")]
    [Arguments("file:///srv/git/tools", "tools")]
    public async Task Folder_IsTheLastSegment_WithoutDotGit(string url, string folder) =>
        await Assert.That(RepoNames.Folder(url)).IsEqualTo(folder);

    [Test]
    [Arguments("https://")]
    [Arguments("https://host/.git")]
    [Arguments("https://host/..")]
    [Arguments("git@host:")]
    public async Task Folder_NoNameLeft_IsNull(string url) =>
        await Assert.That(RepoNames.Folder(url)).IsNull();

    [Test]
    public async Task Branch_IsArmAndTheFirst8CharactersOfTheSessionId() =>
        await Assert.That(RepoNames.Branch(Guid.Parse("d3b07384-d9a0-4c9b-8f3e-0123456789ab"))).IsEqualTo("arm/d3b07384");

    [Test]
    public async Task Shared_IsOnePerUrl_NamedAfterTheRepository()
    {
        var a = RepoNames.Shared("git@github.com:a/tools.git");
        var b = RepoNames.Shared("git@github.com:b/tools.git");

        await Assert.That(a).StartsWith("tools-");
        await Assert.That(a).EndsWith(".git");
        await Assert.That(a).IsNotEqualTo(b);
        await Assert.That(RepoNames.Shared("git@github.com:a/tools.git")).IsEqualTo(a);
    }

    [Test]
    public async Task Check_NoReposOrGoodOnes_IsFine()
    {
        await Assert.That(RepoErrors.Check(null)).IsNull();
        await Assert.That(RepoErrors.Check([])).IsNull();
        await Assert.That(RepoErrors.Check([Repo("git@github.com:o/a.git"), Repo("https://github.com/o/b", "main"), Repo("/srv/c", "0123abc")])).IsNull();
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Check_ABlankUrl_IsInvalid(string url)
    {
        var problem = RepoErrors.Check([Repo("git@github.com:o/a.git"), Repo(url)]);

        await Assert.That(problem!.Code).IsEqualTo(ArmErrors.InvalidRequest);
        await Assert.That(problem.Message).IsEqualTo("'repos[1].url' cannot be blank.");
    }

    [Test]
    public async Task Check_TwoInOneFolder_IsInvalid()
    {
        var problem = RepoErrors.Check([Repo("git@github.com:a/tools.git"), Repo("https://github.com/b/tools")]);

        await Assert.That(problem!.Code).IsEqualTo(ArmErrors.InvalidRequest);
        await Assert.That(problem.Message).IsEqualTo("'repos[0]' and 'repos[1]' would both be checked out in the folder 'tools'.");
    }

    [Test]
    [Arguments("https://host/.git")]
    [Arguments("--upload-pack=touch /tmp/x")]
    public async Task Check_AUrlWithoutAFolderOrLikeAnOption_IsInvalid(string url) =>
        await Assert.That(RepoErrors.Check([Repo(url)])!.Code).IsEqualTo(ArmErrors.InvalidRequest);

    [Test]
    [Arguments("")]
    [Arguments(" ")]
    [Arguments("--orphan")]
    [Arguments("main branch")]
    public async Task Check_ABadRef_IsInvalid(string gitRef) =>
        await Assert.That(RepoErrors.Check([Repo("https://host/a", gitRef)])!.Message).StartsWith("'repos[0].ref' must be a branch, tag or commit");

    private static CreateSessionRepo Repo(string url, string? gitRef = null) => new() { Url = url, Ref = gitRef };
}
