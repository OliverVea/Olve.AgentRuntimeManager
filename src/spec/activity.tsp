import "@typespec/http";

using Http;

namespace Arm;

/** What a line of a session's activity is. */
enum ActivityKind {
  /** Something the agent said. */
  text,

  /** A tool the agent called: its name and, in short, what it was called with. */
  tool,

  /** Something that failed: a tool call, a turn, or the session itself. */
  error,
}

/** One line of a session's activity. */
model ActivityLine {
  kind: ActivityKind;

  /** A single line for people: no line breaks, shortened when long. */
  text: string;
}

/**
 * What a session's agent has been doing most recently, for people to glance at: its own words,
 * the tools it called and what failed. Not everything: thinking, tool results and the rest are in
 * the session's conversation.
 */
model Activity {
  /** The most recent lines, oldest first. */
  lines: ActivityLine[];
}

@route("/sessions/{id}/activity")
interface SessionActivity {
  /**
   * The session's most recent activity. Only the recent end of the conversation is searched (for
   * `claude`, the last 1 MiB of the agent's output, skipping any single event over 256 KiB): when
   * nothing there is worth showing (e.g. a subagent has been busy for long), fewer lines come back,
   * or none.
   */
  @get get(
    ...SessionId,

    /** How many lines at most (the most recent ones). */
    @query @minValue(1) @maxValue(50) limit?: int32 = 3,
  ): Activity | BadRequest | NotFound | UnauthorizedResponse;
}
