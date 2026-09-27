import "@typespec/http";

using Http;

namespace Arm;

/** What a conversation entry is. */
enum ConversationEntryKind {
  /** A message to the agent: the session's prompt (and, after a restart, the resume message), or a subagent's task. */
  prompt,

  /** The agent's text. */
  text,

  /** The agent's thinking, when the provider shows it. */
  thinking,

  /** The agent called a tool: `tool` and `input`, and `toolId` to match its result. */
  tool_call,

  /** A tool's result: `toolId`, the result as `text`, `isError` when it failed. */
  tool_result,

  /** The end of one of the agent's turns: `isError` when the turn failed, `text` its final answer or error. */
  turn_end,

  /**
   * Something the agent's own tooling told it, not a message from anyone: e.g. that a command it
   * started in the background has finished. `text` as the provider gave it.
   */
  notice,
}

/** One entry of a session's conversation. */
model ConversationEntry {
  /** Its position in the session's conversation, from 1; entries never change or move. */
  seq: int32;

  kind: ConversationEntryKind;

  /** When the provider says it happened. */
  at?: utcDateTime;

  /** The text: a prompt, the agent's text or thinking, a tool's result, a turn's answer. */
  text?: string;

  /** Set for `tool_call`: the tool's name. */
  tool?: string;

  /** Set for `tool_call`: its arguments, as the agent gave them. */
  input?: unknown;

  /** Set for `tool_call` and `tool_result`: the call's id, which pairs them. */
  toolId?: string;

  /** Set for `tool_result` and `turn_end`: whether it failed. */
  isError?: boolean;

  /** Set when a subagent made the entry: the `toolId` of the tool call that started it. */
  parentToolId?: string;
}

/** A session's conversation so far: every entry in order, and counts. */
model Conversation {
  entries: ConversationEntry[];

  /** The agent's turns that have ended. */
  turns: int32;

  /** Messages between the user and the agent (prompts, for now). */
  messages: int32;

  toolCalls: int32;

  /** The last entry's `seq` (0: none yet). Live updates (M6) apply only entries after it. */
  lastSeq: int32;
}

@route("/sessions/{id}/conversation")
interface SessionConversation {
  /** The session's conversation: everything its agent was told and did, in order. */
  @get get(...SessionId): Conversation | BadRequest | NotFound | UnauthorizedResponse;
}
