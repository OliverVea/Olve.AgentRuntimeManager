using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Olve.AgentRuntimeManager.Sessions.Skills;

namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>
/// The skills registry (<c>/api/skills</c>, M10b) and a session's <c>skills</c>. The target is shared
/// (a deployed one too), so each test installs names of its own and removes them. Installing needs
/// the supervisor's <c>skill-install</c> on a registry the test can look at, and a git repository
/// it makes in a temp folder: those tests run in-process only.
/// </summary>
[ClassDataSource<ApiTarget>(Shared = SharedType.PerAssembly)]
public class SkillTests(ApiTarget target)
{
    private static string UniqueName() => $"api-test-{Guid.NewGuid():N}";

    [Test]
    public async Task List_NeedsAToken()
    {
        using var response = await target.CreateClient().GetAsync("/api/skills");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task List_IsEveryRegisteredSkill_NothingElse()
    {
        var skills = (await target.CreateAuthenticatedClient().GetFromJsonAsync<SkillBody[]>("/api/skills", Wire.JsonOptions))!;

        // Other tests install and remove their own at the same time; none is ever this one.
        await Assert.That(skills.Select(s => s.Name)).DoesNotContain(UniqueName());
        await Assert.That(skills.Select(s => s.Name).Distinct().Count()).IsEqualTo(skills.Length);
    }

    [Test]
    public async Task Get_AnUnknownSkill_Is404()
    {
        using var response = await target.CreateAuthenticatedClient().GetAsync($"/api/skills/{UniqueName()}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await response.ErrorAsync()).Code).IsEqualTo("SKILL_NOT_FOUND");
    }

    [Test]
    public async Task Delete_AnUnknownSkill_Is404()
    {
        using var response = await target.CreateAuthenticatedClient().DeleteAsync($"/api/skills/{UniqueName()}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await response.ErrorAsync()).Code).IsEqualTo("SKILL_NOT_FOUND");
    }

    [Test]
    [Arguments("Bad_Name")]
    [Arguments("-leading-dash")]
    [Arguments("UPPER")]
    public async Task Install_AnInvalidName_Is400(string name)
    {
        using var response = await target.CreateAuthenticatedClient().PutAsync($"/api/skills/{name}",
            Wire.JsonContent(new InstallSkillBody(new SkillSourceBody("https://example.invalid/skills.git"))));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await response.ErrorAsync()).Code).IsEqualTo("INVALID_REQUEST");
    }

    [Test]
    public async Task Install_FromARepository_ReturnsIt_ListsIt_GetsIt_Replaces_ThenDelete_RemovesIt()
    {
        var root = InProcessRegistry();
        var client = target.CreateAuthenticatedClient();
        var name = UniqueName();
        var repository = LocalGit.Repository(new Dictionary<string, string>
        {
            ["skills/pdf/SKILL.md"] = LocalGit.SkillFile("pdf", "Reads and writes PDF files."),
            ["skills/pdf/scripts/extract.sh"] = "#!/bin/sh\npdftotext \"$1\"\n",
            ["SKILL.md"] = LocalGit.SkillFile("root", "The repository's own skill, at its root."),
        });

        using var installed = await client.PutAsync($"/api/skills/{name}",
            Wire.JsonContent(new InstallSkillBody(new SkillSourceBody(repository, Path: "skills/pdf"))));
        var skill = (await installed.Content.ReadFromJsonAsync<SkillBody>(Wire.JsonOptions))!;
        var listed = (await client.GetFromJsonAsync<SkillBody[]>("/api/skills", Wire.JsonOptions))!.SingleOrDefault(s => s.Name == name);
        var got = await client.GetFromJsonAsync<SkillBody>($"/api/skills/{name}", Wire.JsonOptions);
        var folder = Path.Combine(root, name);
        var installedFiles = Directory.Exists(folder) ? Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(folder, f)).Order().ToArray() : [];

        using var replaced = await client.PutAsync($"/api/skills/{name}",
            Wire.JsonContent(new InstallSkillBody(new SkillSourceBody(repository, Ref: "main"), Default: true)));
        var replacement = (await replaced.Content.ReadFromJsonAsync<SkillBody>(Wire.JsonOptions))!;
        var replacedFiles = Directory.Exists(folder) ? Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(folder, f)).Order().ToArray() : [];

        using var deleted = await client.DeleteAsync($"/api/skills/{name}");
        using var gone = await client.GetAsync($"/api/skills/{name}");
        using var again = await client.DeleteAsync($"/api/skills/{name}");

        await Assert.That(installed.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(skill with { UpdatedAt = default })
            .IsEqualTo(new SkillBody(name, "Reads and writes PDF files.", false, new SkillSourceBody(repository, null, "skills/pdf"), default));
        await Assert.That(listed).IsEqualTo(skill);
        await Assert.That(got).IsEqualTo(skill);
        await Assert.That(installedFiles).IsEquivalentTo(["SKILL.md", Path.Combine("scripts", "extract.sh")]);
        await Assert.That(replaced.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(replacement with { UpdatedAt = default })
            .IsEqualTo(new SkillBody(name, "The repository's own skill, at its root.", true, new SkillSourceBody(repository, "main", null), default));
        await Assert.That(replacement.UpdatedAt).IsGreaterThanOrEqualTo(skill.UpdatedAt);
        await Assert.That(replacedFiles).Contains("SKILL.md");
        await Assert.That(deleted.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        await Assert.That(gone.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(again.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(Directory.Exists(folder)).IsFalse();
    }

    [Test]
    public async Task Install_FromARepositoryThatCantBeFetched_Is400_WithGitsMessage()
    {
        InProcessRegistry();
        LocalGit.Require();
        var name = UniqueName();
        var missing = Path.Combine(Path.GetTempPath(), $"arm-no-such-repo-{Guid.NewGuid():N}");

        using var response = await target.CreateAuthenticatedClient().PutAsync($"/api/skills/{name}", Wire.JsonContent(new InstallSkillBody(new SkillSourceBody(missing))));
        using var unregistered = await target.CreateAuthenticatedClient().GetAsync($"/api/skills/{name}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var error = await response.ErrorAsync();
        await Assert.That(error.Code).IsEqualTo("SKILL_SOURCE_FAILED");
        await Assert.That(error.Message).Contains(missing);
        await Assert.That(unregistered.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Install_AnUnknownRef_Is400_SkillSourceFailed()
    {
        InProcessRegistry();
        var name = UniqueName();
        var repository = LocalGit.Repository(new Dictionary<string, string> { ["SKILL.md"] = LocalGit.SkillFile("x", "A skill.") });

        using var response = await target.CreateAuthenticatedClient().PutAsync($"/api/skills/{name}",
            Wire.JsonContent(new InstallSkillBody(new SkillSourceBody(repository, Ref: "no-such-branch"))));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await response.ErrorAsync()).Code).IsEqualTo("SKILL_SOURCE_FAILED");
    }

    [Test]
    public async Task Install_APathWithoutASkillFile_Is400_InvalidSkill()
    {
        var root = InProcessRegistry();
        var name = UniqueName();
        var repository = LocalGit.Repository(new Dictionary<string, string> { ["docs/README.md"] = "# Not a skill\n" });

        using var response = await target.CreateAuthenticatedClient().PutAsync($"/api/skills/{name}",
            Wire.JsonContent(new InstallSkillBody(new SkillSourceBody(repository, Path: "docs"))));
        using var unregistered = await target.CreateAuthenticatedClient().GetAsync($"/api/skills/{name}");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await response.ErrorAsync()).Code).IsEqualTo("INVALID_SKILL");
        await Assert.That(unregistered.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That(Directory.Exists(Path.Combine(root, name))).IsFalse();
    }

    [Test]
    public async Task Install_ASkillFileWithoutADescription_Is400_InvalidSkill()
    {
        InProcessRegistry();
        var name = UniqueName();
        var repository = LocalGit.Repository(new Dictionary<string, string> { ["SKILL.md"] = "---\nname: x\n---\n\nNo description.\n" });

        using var response = await target.CreateAuthenticatedClient().PutAsync($"/api/skills/{name}", Wire.JsonContent(new InstallSkillBody(new SkillSourceBody(repository))));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await response.ErrorAsync()).Code).IsEqualTo("INVALID_SKILL");
    }

    [Test]
    public async Task Session_WithUnknownSkills_Is400_NamingThem()
    {
        var (first, second) = (UniqueName(), UniqueName());

        using var response = await target.CreateAuthenticatedClient().PostAsync("/api/sessions",
            Wire.JsonContent(new CreateSessionBody("fake:sleep=0ms") { Skills = [first, second, first] }));

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        var error = await response.ErrorAsync();
        await Assert.That(error.Code).IsEqualTo("UNKNOWN_SKILL");
        await Assert.That(error.Message).StartsWith($"No skill '{first}', '{second}'. ");
    }

    [Test]
    public async Task Session_WithAnEmptyList_HasNoSkills()
    {
        var session = await target.CreateAuthenticatedClient().CreateSessionAsync(new CreateSessionBody("fake:sleep=0ms") { Skills = [] });

        await Assert.That(session.Skills).IsEquivalentTo(Array.Empty<string>());
    }

    [Test]
    public async Task Session_WithRegisteredSkills_HasThemAsGiven()
    {
        InProcessRegistry();
        var client = target.CreateAuthenticatedClient();
        var (first, second) = (UniqueName(), UniqueName());
        var repository = LocalGit.Repository(new Dictionary<string, string> { ["SKILL.md"] = LocalGit.SkillFile("x", "A skill.") });
        (await client.PutAsync($"/api/skills/{first}", Wire.JsonContent(new InstallSkillBody(new SkillSourceBody(repository))))).EnsureSuccessStatusCode();
        (await client.PutAsync($"/api/skills/{second}", Wire.JsonContent(new InstallSkillBody(new SkillSourceBody(repository))))).EnsureSuccessStatusCode();

        var session = await client.CreateSessionAsync(new CreateSessionBody("fake:sleep=0ms") { Skills = [second, first] });
        (await client.DeleteAsync($"/api/skills/{first}")).Dispose();
        (await client.DeleteAsync($"/api/skills/{second}")).Dispose();

        await Assert.That(session.Skills).IsEquivalentTo([second, first], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Session_WithoutSkills_GetsTheDefaultOnes()
    {
        InProcessRegistry();
        var client = target.CreateAuthenticatedClient();
        var (standard, optional) = (UniqueName(), UniqueName());
        var repository = LocalGit.Repository(new Dictionary<string, string> { ["SKILL.md"] = LocalGit.SkillFile("x", "A skill.") });
        (await client.PutAsync($"/api/skills/{standard}", Wire.JsonContent(new InstallSkillBody(new SkillSourceBody(repository), Default: true)))).EnsureSuccessStatusCode();
        (await client.PutAsync($"/api/skills/{optional}", Wire.JsonContent(new InstallSkillBody(new SkillSourceBody(repository), Default: false)))).EnsureSuccessStatusCode();

        var session = await client.CreateSessionAsync("fake:sleep=0ms");
        (await client.DeleteAsync($"/api/skills/{standard}")).Dispose();
        (await client.DeleteAsync($"/api/skills/{optional}")).Dispose();

        // Other tests' defaults may be there too (installed at the same time); this one's are stated exactly.
        await Assert.That(session.Skills).IsNotNull();
        await Assert.That(session.Skills!).Contains(standard);
        await Assert.That(session.Skills!).DoesNotContain(optional);
    }

    /// <summary>The in-process host's skills registry (its root); skips the test on a deployed target, whose registry can't be looked at.</summary>
    private string InProcessRegistry() =>
        target.Factory.Services.GetRequiredService<IOptions<SkillsOptions>>().Value.ResolvedRoot();
}
