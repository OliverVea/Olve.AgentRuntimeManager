using Microsoft.Extensions.Options;
using Olve.AgentRuntimeManager.Sessions.Conversations;

namespace Olve.AgentRuntimeManager.Sessions.Providers;

/// <summary>
/// A provider that runs no LLM: each agent follows the <see cref="FakeScript"/> in its prompt. The
/// default provider until real ones land (M9), and what every test runs against.
/// </summary>
public sealed class FakeProvider(TimeProvider time, IOptions<FakeProviderOptions> options) : IAgentProvider
{
    public const string ProviderName = "fake";

    private readonly FakeConversations _conversations = new();

    public string Name => ProviderName;

    public IAgentRun Start(AgentLaunch launch)
    {
        var script = FakeScript.Parse(launch.Prompt, options.Value.Delay);
        var conversation = new Action<IEnumerable<ConversationEntryRecord>>(entries => _conversations.Add(launch.SessionId, entries));
        return new FakeRun(script, launch.Attempt, time, launch.Prompt, conversation);
    }

    public IReadOnlyList<ConversationEntryRecord> Conversation(Guid sessionId) => _conversations.Of(sessionId);
}
