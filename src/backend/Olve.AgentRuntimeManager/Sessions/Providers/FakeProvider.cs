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

    /// <summary>
    /// Starts a fake agent following the directives in everything it's told first (the prompt and
    /// held messages; a continuing session: its messages), keeping its provider session id when it resumes.
    /// </summary>
    public IAgentRun Start(AgentLaunch launch)
    {
        var input = launch.Input;
        var script = FakeScript.Parse(string.Join('\n', input), options.Value.Delay);
        var conversation = new Action<IEnumerable<ConversationEntryRecord>>(entries => _conversations.Add(launch.SessionId, entries));
        return new FakeRun(script, launch.Attempt, time, input, conversation, launch.Resume);
    }

    public IReadOnlyList<ConversationEntryRecord> Conversation(Guid sessionId) => _conversations.Of(sessionId);
}
