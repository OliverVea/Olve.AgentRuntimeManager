import type { ConversationEntry } from "@arm/client";
import { escapeHtml } from "../base-element.js";
import { duration } from "./format.js";

/** Lines of a tool result shown before "Show all". */
export const RESULT_LINES = 12;

/** URLs become links; not trailing punctuation or a closing backtick. Runs on escaped HTML. */
function linkify(html: string): string {
  return html.replace(
    /https?:\/\/[^\s<"`]*[^\s<"`.,;:)]/g,
    (url) => `<a href="${url}" target="_blank" rel="noopener">${url}</a>`,
  );
}

/** `code`, **bold** and links in one escaped line. */
export function inline(text: string): string {
  return linkify(
    escapeHtml(text)
      .replace(/`([^`]+)`/g, "<code>$1</code>")
      .replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>"),
  );
}

/** Small markdown for prompts and agent text: fenced code, headings, lists, paragraphs. */
export function markdown(text: string): string {
  return text
    .split(/```[^\n]*\n?/)
    .map((part, i) => {
      if (i % 2) return `<pre class="fence">${escapeHtml(part.replace(/\n$/, ""))}</pre>`;
      return part
        .split(/\n{2,}/)
        .filter((block) => block.trim())
        .map((block) => {
          const lines = block.split("\n");
          if (lines.every((l) => /^\s*[-*] /.test(l))) {
            return `<ul>${lines.map((l) => `<li>${inline(l.replace(/^\s*[-*] /, ""))}</li>`).join("")}</ul>`;
          }
          if (/^#{1,6} /.test(block))
            return `<p><strong>${inline(block.replace(/^#+ /, ""))}</strong></p>`;
          return `<p>${lines.map(inline).join("<br>")}</p>`;
        })
        .join("");
    })
    .join("");
}

/** A shell command without its set-up (`cd`, `export`, `eval`), which says nothing about what it does. */
export function shellGist(command: string): string {
  const parts = (command.split("\n")[0] ?? "")
    .split(/\s*(?:&&|;)\s*/)
    .filter((p) => p && !/^(cd|export|eval)\b/.test(p));
  return parts.join(" && ") || command;
}

type Input = Record<string, unknown>;
const inputOf = (call: ConversationEntry): Input =>
  call.input && typeof call.input === "object" ? (call.input as Input) : {};

/** What a tool call is about, in one line: its description, command, file, pattern or URL. */
export function gist(call: ConversationEntry): string {
  const i = inputOf(call);
  const text = (key: string) => (typeof i[key] === "string" ? (i[key] as string) : undefined);
  const command = text("command");
  return (
    text("description") ??
    (command !== undefined ? shellGist(command) : undefined) ??
    text("file_path") ??
    text("pattern") ??
    text("url") ??
    JSON.stringify(call.input ?? {})
  );
}

/** One line of a result: the first with something in it, or for an error the one that says what went wrong. */
export function preview(result: ConversationEntry): string {
  const lines = (result.text ?? "")
    .split("\n")
    .map((l) => l.trim())
    .filter(Boolean);
  const line = result.isError
    ? (lines.find((l) => /error|fatal|denied|failed|exception/i.test(l)) ?? lines.at(-1))
    : lines[0];
  return line ?? "(no output)";
}

/** Time taken between two entries: `<0.1s`, `0.4s`, `12s`, `2m`; empty without both times. */
export function took(from: string | undefined, to: string | undefined): string {
  if (!from || !to) return "";
  const ms = Math.max(0, Date.parse(to) - Date.parse(from));
  if (ms < 100) return "<0.1s";
  if (ms < 10_000) return `${(ms / 1000).toFixed(1)}s`;
  const seconds = Math.round(ms / 1000);
  if (seconds < 60) return `${seconds}s`;
  return seconds < 3600 && seconds % 60
    ? `${Math.floor(seconds / 60)}m ${seconds % 60}s`
    : duration(seconds);
}

/** How far into the conversation an entry is: `+1:45`. */
export function offset(at: string, start: string): string {
  const s = Math.max(0, Math.round((Date.parse(at) - Date.parse(start)) / 1000));
  return `+${Math.floor(s / 60)}:${String(s % 60).padStart(2, "0")}`;
}

/** A clock time with seconds: `15:15:27`. */
export function clockSeconds(at: string): string {
  return new Date(at).toLocaleTimeString([], {
    hour: "2-digit",
    minute: "2-digit",
    second: "2-digit",
    hour12: false,
  });
}

export type InputField =
  | { kind: "short"; name: string; value: string }
  | { kind: "text"; name: string; value: string; shell: boolean }
  | { kind: "diff"; before: string; after: string };

/**
 * A tool's input as fields: short values (numbers, flags, one-line strings) as `name value`, text
 * as real multi-line text, and an Edit's `old_string`/`new_string` as a diff.
 */
export function inputFields(call: ConversationEntry): InputField[] {
  const input = inputOf(call);
  const long = (v: unknown) => typeof v === "string" && (v.includes("\n") || v.length > 80);
  const edit = call.tool === "Edit" && "old_string" in input;
  const fields: InputField[] = [];
  for (const [name, value] of Object.entries(input)) {
    if (edit && /_string$/.test(name)) continue;
    if (!long(value) && (typeof value !== "object" || value === null)) {
      fields.push({ kind: "short", name, value: String(value) });
    }
  }
  if (edit) {
    fields.push({
      kind: "diff",
      before: String(input.old_string ?? ""),
      after: String(input.new_string ?? ""),
    });
    return fields;
  }
  for (const [name, value] of Object.entries(input)) {
    if (long(value) || (typeof value === "object" && value !== null)) {
      fields.push({
        kind: "text",
        name,
        value: typeof value === "string" ? value : JSON.stringify(value, null, 2),
        shell: name === "command",
      });
    }
  }
  return fields;
}

/**
 * The entries shown at the top level, in order: tool results show with their call, a subagent's
 * entries under the call that started it, and agent text that's also the turn's answer only once.
 */
export function topLevel(entries: readonly ConversationEntry[]): ConversationEntry[] {
  return entries.filter(
    (e, i) =>
      !e.parentToolId &&
      e.kind !== "tool_result" &&
      !(
        e.kind === "text" &&
        entries[i + 1]?.kind === "turn_end" &&
        entries[i + 1]?.text === e.text
      ),
  );
}

/** The message that began the turn a `turn_end` ends: the last top-level prompt before it. */
export function turnStart(
  entries: readonly ConversationEntry[],
  end: ConversationEntry,
): ConversationEntry | undefined {
  return [...entries]
    .reverse()
    .find((e) => e.seq < end.seq && e.kind === "prompt" && !e.parentToolId);
}
