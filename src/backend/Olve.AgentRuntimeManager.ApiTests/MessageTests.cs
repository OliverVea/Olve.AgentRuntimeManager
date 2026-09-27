using System.Net;
using System.Net.Http.Json;

namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>
/// <c>POST /api/sessions/{id}/messages</c> (M11) on the fake provider: a message to a running fake
/// agent shows in its conversation as a prompt (with the steps its <c>fake:</c> directives script),
/// and an ended fake session runs again. Messages to a queued session: <see cref="QueueTests"/>.
/// </summary>
[ClassDataSource<ApiTarget>(Shared = SharedType.PerAssembly)]
public class MessageTests(ApiTarget target)
{
    [Test]
    public async Task ToAWorkingSession_IsDelivered_AndShowsInItsConversation()
    {
        var client = target.CreateAuthenticatedClient();
        var session = await client.CreateHangingSessionAsync();
        await client.WaitForSessionAsync(session.Id, s => s.Status == "working");

        var delivery = await client.SentMessageAsync(session.Id, "Also this. fake:say=Noted");
        var conversation = (await client.GetFromJsonAsync<ConversationBody>($"/api/sessions/{session.Id}/conversation", Wire.JsonOptions))!;
        await client.KillSessionAsync(session.Id);

        await Assert.That(delivery).IsEqualTo("delivered");
        await Assert.That(conversation.Entries.Select(e => (e.Kind, e.Text ?? ""))).IsEquivalentTo(
            [("prompt", "Wait. fake:hang"), ("prompt", "Also this. fake:say=Noted"), ("text", "Noted")], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task ToAnEndedSession_ContinuesIt_WithItsConversation()
    {
        var client = target.CreateAuthenticatedClient();
        var session = await client.CreateSessionAsync("fake:sleep=0ms fake:say=First fake:exit=3");
        var ended = await client.WaitForSessionAsync(session.Id, s => s.Status == "completed");

        var delivery = await client.SentMessageAsync(session.Id, "And now? fake:sleep=0ms fake:say=Second");
        var continued = await client.WaitForSessionAsync(session.Id, s => s is { Status: "completed", Attempts: 2 });
        var conversation = (await client.GetFromJsonAsync<ConversationBody>($"/api/sessions/{session.Id}/conversation", Wire.JsonOptions))!;

        await Assert.That(delivery).IsEqualTo("continued");
        await Assert.That(continued.ExitCode).IsEqualTo(0);
        await Assert.That(continued.ProviderSessionId).IsEqualTo(ended.ProviderSessionId);
        await Assert.That(continued.StartedAt!.Value).IsGreaterThanOrEqualTo(ended.EndedAt!.Value);
        await Assert.That(conversation.Entries.Select(e => (e.Kind, e.Text ?? ""))).IsEquivalentTo(
            [
                ("prompt", "fake:sleep=0ms fake:say=First fake:exit=3"), ("text", "First"), ("turn_end", "Done."),
                ("prompt", "And now? fake:sleep=0ms fake:say=Second"), ("text", "Second"), ("turn_end", "Done."),
            ],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task ToAKilledSession_ContinuesIt_ClearingHowItEnded()
    {
        var client = target.CreateAuthenticatedClient();
        var session = await client.CreateHangingSessionAsync();
        (await client.KillSessionAsync(session.Id, "enough")).EnsureSuccessStatusCode();

        var delivery = await client.SentMessageAsync(session.Id, "fake:hang");
        var continued = await client.WaitForSessionAsync(session.Id, s => s.Status == "working");
        await client.KillSessionAsync(session.Id);

        await Assert.That(delivery).IsEqualTo("continued");
        await Assert.That(continued.KillReason).IsNull();
        await Assert.That(continued.KillSource).IsNull();
        await Assert.That(continued.EndedAt).IsNull();
    }

    [Test]
    public async Task ToASessionWhoseAgentNeverStarted_Is409()
    {
        var client = target.CreateAuthenticatedClient();
        // An unknown directive: the fake agent can't start, and the session fails without one.
        var session = await client.CreateSessionAsync("fake:bogus");
        await client.WaitForSessionAsync(session.Id, s => s.Status == "failed");

        using var response = await client.SendMessageAsync(session.Id, "hello?");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That((await response.ErrorAsync()).Code).IsEqualTo("SESSION_NEVER_STARTED");
        await Assert.That((await client.GetSessionAsync(session.Id)).Status).IsEqualTo("failed");
    }

    [Test]
    public async Task ToAnUnknownSession_Is404()
    {
        using var response = await target.CreateAuthenticatedClient().SendMessageAsync(Guid.NewGuid(), "hello?");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await response.ErrorAsync()).Code).IsEqualTo("SESSION_NOT_FOUND");
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task BlankText_Is400(string text)
    {
        var client = target.CreateAuthenticatedClient();
        var session = await client.CreateSessionAsync("fake:sleep=0ms");
        await client.WaitForSessionAsync(session.Id, s => s.Status == "completed");

        using var response = await client.SendMessageAsync(session.Id, text);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That((await response.ErrorAsync()).Code).IsEqualTo("INVALID_REQUEST");
        await Assert.That((await client.GetSessionAsync(session.Id)).Status).IsEqualTo("completed");
    }

    [Test]
    public async Task Message_NeedsAToken()
    {
        using var response = await target.CreateClient().SendMessageAsync(Guid.NewGuid(), "hello?");

        await Assert.That((int)response.StatusCode).IsEqualTo(401);
    }
}
