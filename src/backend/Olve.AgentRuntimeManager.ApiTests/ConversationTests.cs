using System.Net;
using System.Net.Http.Json;

namespace Olve.AgentRuntimeManager.ApiTests;

/// <summary>
/// <c>GET /api/sessions/{id}/conversation</c> on the fake provider, whose <c>fake:say=</c> and
/// <c>fake:tool=</c> directives script what its conversation shows.
/// </summary>
[ClassDataSource<ApiTarget>(Shared = SharedType.PerAssembly)]
public class ConversationTests(ApiTarget target)
{
    [Test]
    public async Task EndedSession_HasItsWholeConversation_Numbered()
    {
        var client = target.CreateAuthenticatedClient();
        const string prompt = "Do it. fake:sleep=0ms fake:say=Looking fake:tool=Bash fake:say=All_done";
        var session = await client.CreateSessionAsync(prompt);
        await client.WaitForSessionAsync(session.Id, s => s.Status == "completed");

        var conversation = (await client.GetFromJsonAsync<ConversationBody>($"/api/sessions/{session.Id}/conversation", Wire.JsonOptions))!;

        await Assert.That(conversation.Entries.Select(e => e.Kind)).IsEquivalentTo(
            ["prompt", "text", "tool_call", "tool_result", "text", "turn_end"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(conversation.Entries.Select(e => e.Seq)).IsEquivalentTo([1, 2, 3, 4, 5, 6], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(conversation.Entries[0].Text).IsEqualTo(prompt);
        await Assert.That(conversation.Entries[1].Text).IsEqualTo("Looking");
        await Assert.That(conversation.Entries[2].Tool).IsEqualTo("Bash");
        await Assert.That(conversation.Entries[3].ToolId).IsEqualTo(conversation.Entries[2].ToolId);
        await Assert.That(conversation.Entries[5].IsError).IsFalse();
        await Assert.That((conversation.Turns, conversation.Messages, conversation.ToolCalls, conversation.LastSeq)).IsEqualTo((1, 1, 1, 6));
    }

    [Test]
    public async Task FailedSession_EndsWithItsError()
    {
        var client = target.CreateAuthenticatedClient();
        var session = await client.CreateSessionAsync("fake:sleep=0ms fake:fail=out_of_tokens");
        await client.WaitForSessionAsync(session.Id, s => s.Status == "failed");

        var conversation = (await client.GetFromJsonAsync<ConversationBody>($"/api/sessions/{session.Id}/conversation", Wire.JsonOptions))!;

        await Assert.That(conversation.Entries[^1]).IsEqualTo(conversation.Entries[^1] with { Kind = "turn_end", Text = "out of tokens", IsError = true });
    }

    [Test]
    public async Task UnknownSession_Is404()
    {
        using var response = await target.CreateAuthenticatedClient().GetAsync($"/api/sessions/{Guid.NewGuid()}/conversation");

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NotFound);
        await Assert.That((await response.ErrorAsync()).Code).IsEqualTo("SESSION_NOT_FOUND");
    }

    [Test]
    public async Task Conversation_NeedsAToken()
    {
        using var response = await target.CreateClient().GetAsync($"/api/sessions/{Guid.NewGuid()}/conversation");

        await Assert.That((int)response.StatusCode).IsEqualTo(401);
    }
}
