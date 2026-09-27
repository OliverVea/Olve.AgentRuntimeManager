import type { Conversation, ConversationEntry } from "@arm/client";

/** Lines of a tool result shown before it's cut (the full result is in `--json`). */
const RESULT_LINES = 20;

function indent(text: string, prefix: string): string {
  return text
    .split("\n")
    .map((line) => `${prefix}${line}`)
    .join("\n");
}

function clip(text: string): string {
  const lines = text.split("\n");
  return lines.length <= RESULT_LINES ? text : `${lines.slice(0, RESULT_LINES).join("\n")}\n… ${lines.length - RESULT_LINES} more lines (--json for all)`;
}

/** One entry: a label line, then its text indented; a subagent's entries are indented once more. */
function formatEntry(e: ConversationEntry): string {
  const nest = e.parentToolId ? "  │ " : "";
  const body = (text: string | undefined) => (text ? `\n${indent(text, `${nest}    `)}` : "");
  switch (e.kind) {
    case "prompt":
      return `${nest}▶ Prompt${body(e.text)}`;
    case "text":
      return `${nest}● Agent${body(e.text)}`;
    case "thinking":
      return `${nest}… Thinking${body(e.text)}`;
    case "tool_call":
      return `${nest}→ ${e.tool ?? "tool"}${body(e.input === undefined ? undefined : JSON.stringify(e.input, null, 2))}`;
    case "tool_result":
      return `${nest}${e.isError ? "✗ Tool error" : "← Result"}${body(e.text === undefined ? undefined : clip(e.text))}`;
    case "turn_end":
      return `${nest}${e.isError ? "■ Turn failed" : "■ Turn ended"}${body(e.text)}`;
  }
}

/** The conversation as readable text, then `3 turns · 1 message · 12 tool calls`. */
export function formatConversation(c: Conversation): string {
  if (c.entries.length === 0) return "No conversation yet.";
  const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? "" : "s"}`;
  const footer = [plural(c.turns, "turn"), plural(c.messages, "message"), plural(c.toolCalls, "tool call")].join(" · ");
  return `${c.entries.map(formatEntry).join("\n\n")}\n\n${footer}`;
}
