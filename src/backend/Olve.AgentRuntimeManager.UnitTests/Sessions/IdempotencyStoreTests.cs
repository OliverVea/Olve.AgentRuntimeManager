using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Olve.AgentRuntimeManager.Sessions;

namespace Olve.AgentRuntimeManager.UnitTests.Sessions;

public class IdempotencyStoreTests
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly IdempotencyStore<string> _store;
    private int _calls;

    public IdempotencyStoreTests()
    {
        _store = new IdempotencyStore<string>(_time, Options.Create(new SessionOptions { IdempotencyWindow = TimeSpan.FromHours(24) }));
    }

    private string Act() => $"response {++_calls}";

    [Test]
    public async Task SameKey_ReplaysTheFirstResponse()
    {
        var first = _store.GetOrAct("k", Act, keep: _ => true);
        var second = _store.GetOrAct("k", Act, keep: _ => true);

        await Assert.That(second).IsEqualTo(first);
        await Assert.That(_calls).IsEqualTo(1);
    }

    [Test]
    public async Task NoKey_AlwaysActs()
    {
        _store.GetOrAct(null, Act, keep: _ => true);
        _store.GetOrAct(null, Act, keep: _ => true);

        await Assert.That(_calls).IsEqualTo(2);
    }

    [Test]
    public async Task ResponsesNotKept_AreNotReplayed()
    {
        _store.GetOrAct("k", Act, keep: _ => false);
        var second = _store.GetOrAct("k", Act, keep: _ => true);

        await Assert.That(second).IsEqualTo("response 2");
    }

    [Test]
    public async Task AfterTheWindow_TheKeyActsAgain()
    {
        _store.GetOrAct("k", Act, keep: _ => true);
        _time.Advance(TimeSpan.FromHours(24));

        await Assert.That(_store.GetOrAct("k", Act, keep: _ => true)).IsEqualTo("response 2");
    }
}
