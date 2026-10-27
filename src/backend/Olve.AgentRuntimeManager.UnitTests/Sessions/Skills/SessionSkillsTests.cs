using Olve.AgentRuntimeManager.Sessions.Skills;

namespace Olve.AgentRuntimeManager.UnitTests.Sessions.Skills;

/// <summary>A new session's skills (M10b), as <c>POST /api/sessions</c> resolves them against the registry.</summary>
public class SessionSkillsTests
{
    private readonly InMemorySkillStore _store = new InMemorySkillStore()
        .With("zeta", isDefault: true)
        .With("alpha", isDefault: true)
        .With("mid", isDefault: false);

    [Test]
    public async Task Unset_IsTheDefaultOnes_InNameOrder()
    {
        var (skills, problem) = SessionSkills.Resolve(null, _store);

        await Assert.That(problem).IsNull();
        await Assert.That(skills).IsEquivalentTo(["alpha", "zeta"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Unset_WithNothingRegistered_IsNone()
    {
        var (skills, problem) = SessionSkills.Resolve(null, new InMemorySkillStore());

        await Assert.That(problem).IsNull();
        await Assert.That(skills).IsEquivalentTo(Array.Empty<string>());
    }

    [Test]
    public async Task Empty_IsNone_EvenWithDefaults()
    {
        var (skills, problem) = SessionSkills.Resolve([], _store);

        await Assert.That(problem).IsNull();
        await Assert.That(skills).IsEquivalentTo(Array.Empty<string>());
    }

    [Test]
    public async Task Named_AreKeptAsGiven()
    {
        var (skills, problem) = SessionSkills.Resolve(["mid", "zeta", "mid"], _store);

        await Assert.That(problem).IsNull();
        await Assert.That(skills).IsEquivalentTo(["mid", "zeta", "mid"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task Unknown_IsAProblem_NamingThemOnce_AndTheRegistry()
    {
        var (skills, problem) = SessionSkills.Resolve(["alpha", "nope", "Nope", "nope"], _store);

        await Assert.That(skills).IsNull();
        await Assert.That(problem!.Code).IsEqualTo("UNKNOWN_SKILL");
        await Assert.That(problem.Message).IsEqualTo("No skill 'nope', 'Nope'. Registered skills: alpha, mid, zeta.");
    }

    [Test]
    public async Task Unknown_WithNothingRegistered_SaysSo()
    {
        var (_, problem) = SessionSkills.Resolve(["pdf"], new InMemorySkillStore());

        await Assert.That(problem!.Message).IsEqualTo("No skill 'pdf'. No skills are registered.");
    }
}
