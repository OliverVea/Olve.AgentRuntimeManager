using Olve.AgentRuntimeManager.Api;
using Olve.AgentRuntimeManager.Variables;

namespace Olve.AgentRuntimeManager.UnitTests.Variables;

public class SessionEnvTests
{
    private readonly InMemoryEnvStore _store = new InMemoryEnvStore()
        .With("GIT_AUTHOR_NAME", "Oliver", isDefault: true)
        .With("GH_TOKEN", "token", isDefault: true)
        .With("EXTRA", "extra", isDefault: false);

    [Test]
    public async Task Defaults_ReachEverySession()
    {
        var (env, problem) = SessionEnv.Resolve(Request(), _store);

        await Assert.That(problem).IsNull();
        await Assert.That(env).IsEquivalentTo(new Dictionary<string, string> { ["GIT_AUTHOR_NAME"] = "Oliver", ["GH_TOKEN"] = "token" });
    }

    [Test]
    public async Task NonDefaults_OnlyWhenNamed_AndTheSessionsOwnWin()
    {
        var (env, _) = SessionEnv.Resolve(Request() with
        {
            UseEnv = ["EXTRA"],
            Env = new Dictionary<string, string> { ["GIT_AUTHOR_NAME"] = "Agent", ["MINE"] = "mine" },
        }, _store);

        await Assert.That(env).IsEquivalentTo(new Dictionary<string, string>
        {
            ["GIT_AUTHOR_NAME"] = "Agent", ["GH_TOKEN"] = "token", ["EXTRA"] = "extra", ["MINE"] = "mine",
        });
    }

    [Test]
    public async Task UnknownUseEnv_IsAProblem()
    {
        var (_, problem) = SessionEnv.Resolve(Request() with { UseEnv = ["NOPE"] }, _store);

        await Assert.That(problem!.Code).IsEqualTo("ENV_NOT_FOUND");
    }

    [Test]
    [Arguments("CLAUDE_CODE_OAUTH_TOKEN", "RESERVED_ENV_NAME")]
    [Arguments("HOME", "RESERVED_ENV_NAME")]
    [Arguments("ARM_X", "RESERVED_ENV_NAME")]
    [Arguments("NOT-VALID", "INVALID_REQUEST")]
    public async Task BadOwnNames_AreAProblem(string name, string code)
    {
        var (_, problem) = SessionEnv.Resolve(Request() with { Env = new Dictionary<string, string> { [name] = "x" } }, _store);

        await Assert.That(problem!.Code).IsEqualTo(code);
    }

    private static CreateSession Request() => new() { Prompt = "p", Provider = "fake", Model = "m", Caller = "tests" };
}
