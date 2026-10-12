using System.Net;

namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>
/// A session's <c>repos</c> (M5e) over the API. On the fake provider, which has no workplace and
/// checks nothing out: what's tested here is what the API takes, refuses and returns (the checkout
/// itself is tested against the real supervisor in the unit tests).
/// </summary>
[ClassDataSource<ApiTarget>(Shared = SharedType.PerAssembly)]
public class RepoTests(ApiTarget target)
{
    [Test]
    public async Task Session_ReturnsItsReposAsGiven()
    {
        RepoBody[] repos = [new("git@github.com:OliverVea/Olve.AgentRuntimeManager.git"), new("https://github.com/OliverVea/Olve.Utilities", "v1.2.0")];

        var session = await target.CreateAuthenticatedClient().CreateSessionAsync(new CreateSessionBody("fake:sleep=0ms") { Repos = repos });

        await Assert.That(session.Repos).IsEquivalentTo(repos);
    }

    [Test]
    public async Task Session_WithoutRepos_HasNone()
    {
        var session = await target.CreateAuthenticatedClient().CreateSessionAsync("fake:sleep=0ms");

        await Assert.That(session.Repos).IsNull();
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task Session_WithABlankRepoUrl_Is400(string url)
    {
        using var response = await Create([new RepoBody(url)]);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await response.ErrorAsync()).Code).IsEqualTo("INVALID_REQUEST");
    }

    [Test]
    public async Task Session_WithTwoReposInOneFolder_Is400()
    {
        using var response = await Create([new RepoBody("git@github.com:a/tools.git"), new RepoBody("https://github.com/b/tools", "main")]);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var error = await response.ErrorAsync();
        await Assert.That(error.Code).IsEqualTo("INVALID_REQUEST");
        await Assert.That(error.Message).IsEqualTo("'repos[0]' and 'repos[1]' would both be checked out in the folder 'tools'.");
    }

    [Test]
    public async Task Session_WithABlankRef_Is400()
    {
        using var response = await Create([new RepoBody("git@github.com:a/tools.git", " ")]);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await response.ErrorAsync()).Code).IsEqualTo("INVALID_REQUEST");
    }

    [Test]
    [Arguments("--upload-pack=touch /tmp/arm-pwned", null)]
    [Arguments("git@github.com:a/tools.git", "--orphan")]
    public async Task Session_WithAUrlOrRefLikeAGitOption_Is400(string url, string? gitRef)
    {
        using var response = await Create([new RepoBody(url, gitRef)]);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await response.ErrorAsync()).Code).IsEqualTo("INVALID_REQUEST");
    }

    private async Task<HttpResponseMessage> Create(IReadOnlyList<RepoBody> repos) =>
        await target.CreateAuthenticatedClient().PostAsync("/api/sessions", Wire.JsonContent(new CreateSessionBody("fake:sleep=0ms") { Repos = repos }));
}
