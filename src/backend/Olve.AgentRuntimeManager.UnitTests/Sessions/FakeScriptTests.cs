using Olve.AgentRuntimeManager.Sessions.Providers;

namespace Olve.AgentRuntimeManager.UnitTests.Sessions;

public class FakeScriptTests
{
    private static readonly TimeSpan Default = TimeSpan.FromSeconds(2);

    [Test]
    public async Task NoDirectives_CompletesAfterTheDefaultDelay() =>
        await Assert.That(FakeScript.Parse("Fix the bug", Default))
            .IsEqualTo(new FakeScript(Default, Hang: false, ExitCode: 0, Failure: null));

    [Test]
    public async Task Directives_AnywhereInThePrompt_AreApplied()
    {
        var script = FakeScript.Parse("Do it fake:sleep=250ms\nthen fake:exit=3", Default);

        await Assert.That(script).IsEqualTo(new FakeScript(TimeSpan.FromMilliseconds(250), false, 3, null));
    }

    [Test]
    [Arguments("fake:sleep=2s", 2000)]
    [Arguments("fake:sleep=1m", 60000)]
    [Arguments("fake:sleep=0ms", 0)]
    public async Task Sleep_ReadsTheUnit(string prompt, int milliseconds) =>
        await Assert.That(FakeScript.Parse(prompt, Default).Delay).IsEqualTo(TimeSpan.FromMilliseconds(milliseconds));

    [Test]
    public async Task HangAndFail_AreRead()
    {
        await Assert.That(FakeScript.Parse("fake:hang", Default).Hang).IsTrue();
        await Assert.That(FakeScript.Parse("fake:fail=out_of_memory", Default).Failure).IsEqualTo("out of memory");
    }

    [Test]
    public async Task Down_IsReadWithItsAttempts()
    {
        var always = FakeScript.Parse("fake:down=limited", Default);
        var once = FakeScript.Parse("fake:down=unreachable:1", Default);

        await Assert.That(always.Down).IsEqualTo(ProviderTrouble.Limited);
        await Assert.That(always.IsDownOn(1) && always.IsDownOn(99)).IsTrue();
        await Assert.That(once.Down).IsEqualTo(ProviderTrouble.Unreachable);
        await Assert.That(once.IsDownOn(1)).IsTrue();
        await Assert.That(once.IsDownOn(2)).IsFalse();
        await Assert.That(FakeScript.Parse("Fix it", Default).IsDownOn(1)).IsFalse();
    }

    [Test]
    [Arguments("fake:down=sideways")]
    [Arguments("fake:down=unreachable:0")]
    [Arguments("fake:down=unreachable:x")]
    [Arguments("fake:down=3")]
    [Arguments("fake:explode")]
    [Arguments("fake:sleep=soon")]
    [Arguments("fake:exit=x")]
    [Arguments("fake:hang=forever")]
    public async Task UnknownOrMalformedDirectives_Throw(string prompt) =>
        await Assert.That(() => FakeScript.Parse(prompt, Default)).Throws<FormatException>();
}
