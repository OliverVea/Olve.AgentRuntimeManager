import type { Conversation, ConversationEntry, Session } from "@arm/client";
import { BaseElement, escapeHtml } from "../base-element.js";
import {
  clockSeconds,
  inputFields,
  markdown,
  noticeText,
  offset,
  preview,
  RESULT_LINES,
  took,
  gist as toolGist,
  topLevel,
  turnStart,
} from "../sessions/conversation.js";
import { clock, isEnded, modelLabel, relative, shortId } from "../sessions/format.js";
import { note, type TimesAs, timeOf, timeTitle } from "./session-card.js";

/** `<use href>` only finds symbols in its own tree, so the page's icons live in its shadow root. */
const sprite = `<svg width="0" height="0" style="position:absolute" aria-hidden="true">
  <symbol id="i-user" viewBox="0 0 24 24"><circle cx="12" cy="8" r="4"/><path d="M4 21a8 8 0 0 1 16 0"/></symbol>
  <symbol id="i-message" viewBox="0 0 24 24"><path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/></symbol>
  <symbol id="i-bulb" viewBox="0 0 24 24"><path d="M15 14c.2-1 .7-1.7 1.5-2.5 1-.9 1.5-2.2 1.5-3.5A6 6 0 0 0 6 8c0 1 .2 2.2 1.5 3.5.7.7 1.3 1.5 1.5 2.5"/><path d="M9 18h6M10 22h4"/></symbol>
  <symbol id="i-info" viewBox="0 0 24 24"><circle cx="12" cy="12" r="10"/><path d="M12 16v-4M12 8h.01"/></symbol>
  <symbol id="i-flag" viewBox="0 0 24 24"><path d="M4 15s1-1 4-1 5 2 8 2 4-1 4-1V3s-1 1-4 1-5-2-8-2-4 1-4 1z"/><path d="M4 22v-7"/></symbol>
  <symbol id="i-wrench" viewBox="0 0 24 24"><path d="M14.7 6.3a1 1 0 0 0 0 1.4l1.6 1.6a1 1 0 0 0 1.4 0l3.77-3.77a6 6 0 0 1-7.94 7.94l-6.91 6.91a2.12 2.12 0 0 1-3-3l6.91-6.91a6 6 0 0 1 7.94-7.94l-3.76 3.76z"/></symbol>
  <symbol id="i-expand" viewBox="0 0 24 24"><path d="M15 3h6v6M9 21H3v-6M21 3l-7 7M3 21l7-7"/></symbol>
  <symbol id="i-collapse" viewBox="0 0 24 24"><path d="M4 14h6v6M20 10h-6V4M14 10l7-7M3 21l7-7"/></symbol>
</svg>`;

const icon = (name: string) => `<svg class="i"><use href="#i-${name}"/></svg>`;

export type TranscriptView = { thinking: boolean; tools: boolean; notices: boolean };

/** A message this page sent, and what the API said became of it (M11). */
export type SentMessage = {
  text: string;
  delivery: "delivered" | "pending" | "continued";
  caller: string;
};

/**
 * `<session-page>` — one session: its card (status · time · outcome, the task, id · model · caller),
 * its details, and its conversation (M5b) as boxes: prompts, agent text, thinking (folded), tool
 * calls paired with their results (a one-line preview; open for the input and the whole result),
 * a subagent's entries under the call that started it, and each turn's answer. Times are how far
 * into the conversation (`+1:45`) or clock times; clicking one switches them.
 *
 * It only reports what the user wants, as bubbling events: `kill-session` / `delete-session` /
 * `copy-id` (detail: the id), `toggle-times`, and `view-change` (detail: a {@link TranscriptView}).
 * Which tool calls are open survives re-renders, so a running session can be re-read under it.
 */
export class SessionPage extends BaseElement {
  static readonly tagName = "session-page";

  #session: Session | undefined;
  #conversation: Conversation | undefined;
  #problem = "";
  #times: TimesAs = "relative";
  #view: TranscriptView = { thinking: false, tools: false, notices: false };
  /** Tool calls (by toolId) the user opened or closed themselves, overriding "open every tool call". */
  readonly #opened = new Map<string, boolean>();
  /** Results the user asked to see whole. */
  readonly #whole = new Set<string>();
  /** Messages sent from this page (the API keeps no record of their delivery), oldest first. */
  #sent: SentMessage[] = [];
  /** The message being typed, kept across renders (a running session re-renders every few seconds). */
  #draft = "";
  #draftFocused = false;
  #caret: [number, number] = [0, 0];
  #sending = false;
  #sendProblem = "";
  #pendingRender = false;

  set session(session: Session | undefined) {
    this.#session = session;
    this.#rerender();
  }

  set conversation(conversation: Conversation | undefined) {
    this.#conversation = conversation;
    this.#rerender();
  }

  /** Shown instead of the page when it can't be loaded. */
  set problem(problem: string) {
    this.#problem = problem;
    this.#rerender();
  }

  set times(times: TimesAs) {
    this.#times = times;
    this.#rerender();
  }

  set view(view: TranscriptView) {
    this.#view = view;
    this.#rerender();
  }

  /** The message was sent: show it with its delivery, and clear the box. */
  sent(message: SentMessage): void {
    this.#sent.push(message);
    this.#draft = "";
    this.#caret = [0, 0];
    this.#sending = false;
    this.#sendProblem = "";
    this.#rerender();
  }

  /** The message wasn't sent: say why, and keep it in the box. */
  sendFailed(problem: string): void {
    this.#sending = false;
    this.#sendProblem = problem;
    this.#rerender();
  }

  /** A new session: forget which calls were opened, what was sent and typed. */
  reset(): void {
    this.#opened.clear();
    this.#whole.clear();
    this.#sent = [];
    this.#draft = "";
    this.#draftFocused = false;
    this.#sending = false;
    this.#sendProblem = "";
    this.#session = undefined;
    this.#conversation = undefined;
    this.#problem = "";
  }

  constructor() {
    super();
    this.root.addEventListener("click", (e) => this.#click(e as MouseEvent));
    // The message box: remember what's typed (and where), so a re-render doesn't lose it.
    this.root.addEventListener("input", (e) => this.#typed(e.target as HTMLElement));
    this.root.addEventListener("keyup", (e) => this.#typed(e.target as HTMLElement));
    this.root.addEventListener("focusin", (e) => {
      if ((e.target as HTMLElement).matches?.("textarea.message")) this.#draftFocused = true;
    });
    this.root.addEventListener("focusout", (e) => {
      if ((e.target as HTMLElement).matches?.("textarea.message")) this.#draftFocused = false;
    });
    this.root.addEventListener("keydown", (e) => {
      const key = e as KeyboardEvent;
      if (
        key.key === "Enter" &&
        (key.ctrlKey || key.metaKey) &&
        (key.target as HTMLElement).matches?.("textarea.message")
      ) {
        key.preventDefault();
        this.#send();
      }
    });
    this.root.addEventListener("submit", (e) => {
      e.preventDefault();
      this.#send();
    });
    // `toggle` doesn't bubble; capture it to remember what the user opened.
    this.root.addEventListener(
      "toggle",
      (e) => {
        const details = e.target as HTMLDetailsElement;
        const id = details.dataset.tool;
        if (id) this.#opened.set(id, details.open);
      },
      true,
    );
  }

  connectedCallback(): void {
    this.render();
  }

  /** One render per task, however many properties change together. */
  #rerender(): void {
    if (this.#pendingRender || !this.isConnected) return;
    this.#pendingRender = true;
    queueMicrotask(() => {
      this.#pendingRender = false;
      this.render();
    });
  }

  protected override styles(): string {
    return pageStyles;
  }

  /** Puts the draft back into the fresh message box, with the focus and caret where they were. */
  protected override afterRender(): void {
    const box = this.root.querySelector<HTMLTextAreaElement>("textarea.message");
    if (!box) return;
    box.value = this.#draft;
    grow(box);
    if (this.#draftFocused) {
      box.focus();
      box.setSelectionRange(this.#caret[0], this.#caret[1]);
    }
  }

  #typed(target: HTMLElement): void {
    if (!(target instanceof HTMLTextAreaElement) || !target.matches("textarea.message")) return;
    this.#draft = target.value;
    this.#caret = [target.selectionStart, target.selectionEnd];
    grow(target);
    const send = this.root.querySelector<HTMLButtonElement>("button.send");
    if (send) send.disabled = this.#sending || !this.#draft.trim();
  }

  #send(): void {
    const text = this.#draft.trim();
    const session = this.#session;
    if (!text || !session || this.#sending) return;
    this.#sending = true;
    this.#sendProblem = "";
    this.dispatchEvent(
      new CustomEvent("send-message", {
        bubbles: true,
        composed: true,
        detail: { id: session.id, text },
      }),
    );
    this.render();
  }

  protected override template(): string {
    if (this.#problem) return `<div class="problem">${escapeHtml(this.#problem)}</div>`;
    const session = this.#session;
    if (!session) return `<div class="loading">Loading…</div>`;
    const now = Date.now();
    return `${sprite}${this.#head(session, now)}${this.#details(session, now)}${this.#transcript(session)}`;
  }

  // --- the card at the top -----------------------------------------------------------------------

  #head(s: Session, now: number): string {
    const title = escapeHtml(timeTitle(s, this.#times, now));
    const id = escapeHtml(s.id);
    const r1 = [
      `<span class="badge ${s.status}">${s.status}</span>`,
      `<button class="dur" data-times title="${title}">${timeOf(s, this.#times, now)}</button>`,
      note(s),
    ].filter(Boolean);
    const ended = isEnded(s);
    const when =
      ended && s.endedAt
        ? `<span><button class="when" data-times title="${title}">ended ${
            this.#times === "relative" ? relative(s.endedAt, now) : clock(s.endedAt, now)
          }</button></span>`
        : "";
    const action = ended
      ? `<button class="ghost" data-delete="${id}">Delete</button>`
      : `<button class="danger" data-kill="${id}">${s.status === "queued" ? "Cancel" : "Kill"}</button>`;
    return `<div class="head ${s.status}">
      <div class="r1">${r1.join(`<span class="sep">·</span>`)}</div>
      <div class="actions">${action}</div>
      <div class="task" title="${escapeHtml(s.prompt)}">${escapeHtml(s.prompt.split("\n")[0] ?? "")}</div>
      <div class="info"><span class="id" data-copy="${id}" title="Copy the full id (${id})">${shortId(s)}</span><span title="${escapeHtml(
        `${s.provider}/${s.model}`,
      )}">${escapeHtml(modelLabel(s))}</span><span>${escapeHtml(s.caller)}</span>${when}</div>
    </div>`;
  }

  #details(s: Session, now: number): string {
    const row = (name: string, value: string | undefined, cls = "", wide = false) =>
      value
        ? `<div class="${wide ? "wide" : ""}"><dt>${name}</dt><dd class="${cls}">${value}</dd></div>`
        : "";
    const times = (iso: string | undefined) =>
      iso
        ? `<button class="when" data-times>${this.#times === "relative" ? relative(iso, now) : clock(iso, now)}</button>`
        : undefined;
    const env = Object.entries(s.env ?? {})
      .map(([k, v]) => `${k}=${v}`)
      .join(" ");
    return `<details class="more"><summary>Details</summary><dl class="meta">${[
      row(
        "id",
        `<span class="copy mono" data-copy="${escapeHtml(s.id)}" title="Copy">${escapeHtml(s.id)}</span>`,
        "",
        true,
      ),
      row("model", escapeHtml(`${s.provider}/${s.model}`)),
      row("created", times(s.createdAt)),
      row("started", times(s.startedAt)),
      row("ended", times(s.endedAt)),
      row("timeout", s.timeoutSeconds ? `${s.timeoutSeconds}s` : undefined),
      row("runs", `${s.attempts} (${s.retriesLeft} retries left)`),
      row("env", env ? escapeHtml(env) : undefined, "mono"),
      row("uses env", s.useEnv?.length ? escapeHtml(s.useEnv.join(", ")) : undefined, "mono"),
      row(
        "provider id",
        s.providerSessionId
          ? `<span class="mono">${escapeHtml(s.providerSessionId)}</span>`
          : undefined,
        "",
        true,
      ),
      row("error", s.error ? escapeHtml(s.error) : undefined, "err", true),
      row("kill reason", s.killReason ? escapeHtml(s.killReason) : undefined, "", true),
    ].join("")}</dl></details>`;
  }

  // --- the transcript ----------------------------------------------------------------------------

  #transcript(s: Session): string {
    const c = this.#conversation;
    const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? "" : "s"}`;
    const counts = c
      ? `<span>${plural(c.turns, "turn")}</span><span>${plural(c.messages, "message")}</span><span>${plural(c.toolCalls, "tool call")}</span>`
      : "";
    const { thinking, tools, notices } = this.#view;
    const toggles = `<span class="toggles">
      <button class="${thinking ? "on" : ""}" data-view="thinking" title="${thinking ? "Hide" : "Show"} thinking" aria-pressed="${thinking}">${icon("bulb")}</button>
      <button class="${notices ? "on" : ""}" data-view="notices" title="${notices ? "Hide" : "Show"} notices (what the agent's own tooling told it)" aria-pressed="${notices}">${icon("info")}</button>
      <button class="${tools ? "on" : ""}" data-view="tools" title="${tools ? "Close" : "Open"} every tool call" aria-pressed="${tools}">${icon(tools ? "collapse" : "expand")}</button>
    </span>`;
    // Sent from this page: each one labels the first matching prompt after the last one matched;
    // those not in the conversation yet show at the end (held ones dashed, in order).
    this.#labels.clear();
    const unseen: SentMessage[] = [];
    let after = 0;
    for (const m of this.#sent) {
      const hit = c?.entries.find(
        (e) =>
          e.kind === "prompt" &&
          !e.parentToolId &&
          e.seq > after &&
          (e.text ?? "").trim() === m.text,
      );
      if (hit) {
        this.#labels.set(hit.seq, m);
        after = hit.seq;
      } else unseen.push(m);
    }
    const pending = (list: SentMessage[]) =>
      list
        .map((m, i) =>
          m.delivery === "pending"
            ? `<div class="e"><div class="t"></div><div class="c"><div class="msg prompt held">${icon("user")}<div class="body"><div class="label">Held<span class="delivery">${i + 1} · sent when the agent starts</span></div><div class="md">${markdown(m.text)}</div></div></div></div></div>`
            : `<div class="e"><div class="t"></div><div class="c"><div class="msg prompt">${icon("user")}<div class="body"><div class="label">Prompt<span class="delivery ${m.delivery}">${m.delivery} · ${escapeHtml(m.caller)}</span></div><div class="md">${markdown(m.text)}</div></div></div></div></div>`,
        )
        .join("");
    let body: string;
    if (!c) body = `<div class="empty">Loading the conversation…</div>`;
    else if (!c.entries.length)
      body = `<div class="empty">${s.status === "queued" ? "Queued: the conversation starts when the agent does." : "No conversation yet."}</div>`;
    else {
      const start = c.entries.find((e) => e.at)?.at;
      body = topLevel(c.entries)
        .filter((e) => notices || e.kind !== "notice")
        .map((e) => this.#entry(e, c.entries, start))
        .join("");
      if (!isEnded(s))
        body += `<div class="following">Refreshes every few seconds while it runs.</div>`;
    }
    body += pending(unseen);
    return `<section class="transcript"><div class="t-head"><span class="counts">${counts}</span>${toggles}</div><div class="t-body">${body}</div>${this.#messageBox(s)}</section>`;
  }

  /** Which sent message labels which prompt (by seq), worked out for each render. */
  readonly #labels = new Map<number, SentMessage>();

  /** The box at the bottom of the transcript, or why there's none (M11). */
  #messageBox(s: Session): string {
    if (!s.providerSessionId && isEnded(s)) {
      return `<div class="no-agent">This session never started, so there's no agent to message.</div>`;
    }
    const hint =
      s.status === "working"
        ? "Working: a message reaches the agent after its current step."
        : s.status === "queued"
          ? "Queued: messages are held and sent when the agent starts, after the prompt."
          : "The agent has finished: a message continues the session, with everything so far in mind.";
    const problem = this.#sendProblem
      ? `<div class="send-problem">${escapeHtml(this.#sendProblem)}</div>`
      : "";
    return `<form class="composer compose">
      <textarea class="message" rows="1" placeholder="Message the agent…" aria-label="Message the agent"></textarea>
      <div class="row"><span class="hint">${hint}</span><span class="spacer"></span><span class="hint">Ctrl+Enter</span><button class="primary send" type="submit" ${this.#sending || !this.#draft.trim() ? "disabled" : ""}>${this.#sending ? "Sending…" : "Send"}</button></div>
      ${problem}
    </form>`;
  }

  /** A prompt this page sent: what became of it, and who sent it. */
  #delivery(e: ConversationEntry): string {
    const m = this.#labels.get(e.seq);
    return m
      ? `<span class="delivery ${m.delivery}">${m.delivery === "pending" ? "held, now sent" : m.delivery} · ${escapeHtml(m.caller)}</span>`
      : "";
  }

  /** The time in the gutter: a button that switches between `+m:ss` and clock times. */
  #stamp(at: string | undefined, start: string | undefined): string {
    if (!at) return "";
    const into = start ? offset(at, start) : "";
    const relativeShown = this.#times === "relative" && into;
    const shown = relativeShown ? into : clockSeconds(at);
    const other = relativeShown ? clockSeconds(at) : into ? `${into} into the conversation` : "";
    return `<button data-times title="${escapeHtml(`${other}. Click to switch.`)}">${shown}</button>`;
  }

  #entry(
    e: ConversationEntry,
    all: readonly ConversationEntry[],
    start: string | undefined,
  ): string {
    const box = (kind: string, name: string, body: string) =>
      `<div class="msg ${kind}">${icon(name)}<div class="body">${body}</div></div>`;
    const row = (gutter: string, content: string) =>
      `<div class="e"><div class="t">${gutter}</div><div class="c">${content}</div></div>`;
    switch (e.kind) {
      case "prompt":
        return row(
          this.#stamp(e.at, start),
          box(
            "prompt",
            "user",
            `<div class="label">Prompt${this.#delivery(e)}</div><div class="md">${markdown(e.text ?? "")}</div>`,
          ),
        );
      case "text":
        return row(
          this.#stamp(e.at, start),
          box("text", "message", `<div class="md">${markdown(e.text ?? "")}</div>`),
        );
      case "notice":
        return row(
          this.#stamp(e.at, start),
          box("notice", "info", escapeHtml(noticeText(e.text ?? ""))),
        );
      case "thinking":
        return row(
          this.#stamp(e.at, start),
          box(
            "thinking",
            "bulb",
            `<details ${this.#view.thinking ? "open" : ""}><summary>Thinking</summary><div class="thought">${escapeHtml(e.text ?? "")}</div></details>`,
          ),
        );
      case "tool_call": {
        const result = all.find((r) => r.kind === "tool_result" && r.toolId === e.toolId);
        const own = e.toolId
          ? all.filter((x) => x.parentToolId === e.toolId && x.kind !== "tool_result")
          : [];
        const sub = own.length
          ? `<div class="subagent"><div class="subagent-head">Subagent</div>${own.map((x) => this.#entry(x, all, start)).join("")}</div>`
          : "";
        return (
          row(
            `${this.#stamp(e.at, start)}<span class="took">${took(e.at, result?.at) ? `(${took(e.at, result?.at)})` : ""}</span>`,
            this.#tool(e, result),
          ) + sub
        );
      }
      case "turn_end": {
        const began = turnStart(all, e);
        const time = took(began?.at, e.at);
        return (
          row(
            "",
            box(
              `answer ${e.isError ? "failed" : ""}`,
              "flag",
              `<div class="md">${markdown(e.text ?? "")}</div>`,
            ),
          ) +
          `<div class="turn-end ${e.isError ? "failed" : ""}">${e.isError ? "Turn failed" : "Turn ended"}${e.at ? ` · ${this.#stamp(e.at, start)}` : ""}${time ? ` <span class="took">(${time})</span>` : ""}</div>`
        );
      }
      default:
        return "";
    }
  }

  #tool(call: ConversationEntry, result: ConversationEntry | undefined): string {
    const id = call.toolId ?? `seq-${call.seq}`;
    const open = this.#opened.get(id) ?? (this.#view.tools || !!result?.isError);
    const state = !result
      ? `<span class="state running">running</span>`
      : result.isError
        ? `<span class="state err">! error</span>`
        : "";
    const input = inputFields(call)
      .map((f) => {
        switch (f.kind) {
          case "short":
            return `<span class="kv"><span class="k">${escapeHtml(f.name)}</span> <span class="v">${escapeHtml(f.value)}</span></span>`;
          case "text":
            return `<div class="field"><div class="k">${escapeHtml(f.name)}</div><pre class="${f.shell ? "shell" : ""}">${escapeHtml(f.value)}</pre></div>`;
          case "diff": {
            const lines = (text: string, sign: string, cls: string) =>
              text
                .split("\n")
                .map((l) => `<div class="${cls}">${sign} ${escapeHtml(l)}</div>`)
                .join("");
            return `<div class="diff">${lines(f.before, "-", "del")}${lines(f.after, "+", "add")}</div>`;
          }
          default:
            return "";
        }
      })
      .join("");
    let output = "";
    if (result) {
      const lines = (result.text ?? "").split("\n");
      const whole = this.#whole.has(id) || lines.length <= RESULT_LINES;
      const shown = whole ? (result.text ?? "") : lines.slice(0, RESULT_LINES).join("\n");
      output = `<div class="sub result">Result</div><pre class="result ${result.isError ? "err" : ""}">${escapeHtml(shown)}</pre>${
        whole
          ? ""
          : `<button class="more" data-whole="${escapeHtml(id)}">Show all ${lines.length} lines</button>`
      }`;
    }
    const line = result
      ? `<span class="preview ${result.isError ? "err" : ""}">${escapeHtml(preview(result))}</span>`
      : "";
    return `<div class="tool ${result?.isError ? "error" : ""}"><details data-tool="${escapeHtml(id)}" ${open ? "open" : ""}>
      <summary><span class="row">${icon("wrench")}<span class="name">${escapeHtml(call.tool ?? "tool")}</span><span class="arg">${escapeHtml(toolGist(call))}</span>${state}</span>${line}</summary>
      <div class="sub">Input</div><div class="input">${input}</div>${output}
    </details></div>`;
  }

  // --- what the user asks for --------------------------------------------------------------------

  #click(e: MouseEvent): void {
    const target = e.target as HTMLElement;
    const hit = target.closest<HTMLElement>(
      "[data-times], [data-copy], [data-kill], [data-delete], [data-view], [data-whole]",
    );
    if (!hit) return;
    const d = hit.dataset;
    const fire = (type: string, detail?: unknown) =>
      this.dispatchEvent(new CustomEvent(type, { bubbles: true, composed: true, detail }));
    if ("times" in d) {
      e.preventDefault();
      fire("toggle-times");
    } else if (d.copy) fire("copy-id", d.copy);
    else if (d.kill) fire("kill-session", d.kill);
    else if (d.delete) fire("delete-session", d.delete);
    else if (d.view === "thinking" || d.view === "tools" || d.view === "notices") {
      this.#view = { ...this.#view, [d.view]: !this.#view[d.view] };
      // "Open every tool call" applies to all of them again.
      if (d.view === "tools") this.#opened.clear();
      fire("view-change", this.#view);
      this.render();
    } else if (d.whole) {
      this.#whole.add(d.whole);
      this.render();
    }
  }
}

/** Grows the message box with its text (one line to start). */
function grow(box: HTMLTextAreaElement): void {
  box.style.height = "auto";
  box.style.height = `${box.scrollHeight + 2}px`;
  box.classList.toggle("multiline", box.value.includes("\n") || box.scrollHeight > 42);
}

const pageStyles = `
  :host { display: block; }
  button { font: inherit; color: inherit; cursor: pointer; }
  svg.i { width: 16px; height: 16px; fill: none; stroke: currentColor; stroke-width: 2; stroke-linecap: round; stroke-linejoin: round; flex: none; }
  .problem, .loading { margin-top: 12px; padding: 24px; text-align: center; color: var(--dim); background: var(--card); border: 1px solid var(--line); border-radius: 10px; }
  .problem { color: var(--danger); }

  /* The card at the top, as in the Overview. */
  .head { display: grid; grid-template-columns: 1fr auto; gap: 2px 12px; margin: 12px 0; background: var(--card); border: 1px solid var(--line); border-left: 3px solid var(--line); border-radius: 9px; padding: 8px 14px; }
  .head.working { border-left-color: var(--working); } .head.queued { border-left-color: var(--queued); }
  .head .r1 { display: flex; align-items: center; gap: 8px; min-width: 0; line-height: var(--row); }
  .head .actions { grid-row: 1 / span 3; grid-column: 2; align-self: start; }
  .head .task { font-size: 15px; font-weight: 600; display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; overflow: hidden; overflow-wrap: anywhere; }
  .head .info { font: 12px var(--mono); color: var(--dim); line-height: var(--row); }
  .head .info > span + span::before { content: "·"; margin: 0 6px; color: var(--faint); }
  .head .info .id { cursor: copy; border-bottom: 1px dotted var(--faint); }
  .head .info .id:hover { color: var(--accent); }
  .badge { font-size: 11px; font-weight: 700; letter-spacing: .04em; text-transform: uppercase; border-radius: 4px; padding: 0 7px; line-height: 18px; flex: none; }
  .badge.working { background: var(--working-bg); color: var(--working); } .badge.queued, .badge.cancelled { background: var(--queued-bg); color: var(--queued); }
  .badge.completed { background: var(--completed-bg); color: var(--completed); } .badge.killed { background: var(--killed-bg); color: var(--killed); }
  .badge.failed { background: var(--failed-bg); color: var(--failed); }
  .sep { color: var(--faint); flex: none; margin: 0 -2px; }
  .note { color: var(--dim); font-size: 13px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; min-width: 0; }
  .note.failed { color: var(--failed); }
  button[data-times] { font: inherit; color: inherit; border: 0; background: none; padding: 0; cursor: pointer; font-variant-numeric: tabular-nums; }
  button[data-times]:hover { text-decoration: underline dotted; }
  .dur, .when { color: var(--dim); flex: none; }
  .ghost, .danger { height: 32px; border-radius: 6px; padding: 0 14px; font-weight: 600; }
  .ghost { border: 1px solid var(--line); background: transparent; } .ghost:hover { background: var(--hover); }
  .danger { border: 0; background: var(--danger-fill); color: var(--on-accent); }

  .more { margin-bottom: 12px; }
  .more > summary { cursor: pointer; color: var(--dim); font-size: 13px; list-style: none; }
  .more > summary::before { content: "▸ "; } .more[open] > summary::before { content: "▾ "; }
  .meta { display: grid; grid-template-columns: repeat(auto-fill, minmax(230px, 1fr)); gap: 0 24px; background: var(--card); border: 1px solid var(--line); border-radius: 10px; padding: 10px 16px; margin: 6px 0 0; }
  .meta > div { display: flex; gap: 10px; min-width: 0; padding: 4px 0; font-size: 13px; }
  .meta .wide { grid-column: span 2; }
  .meta dt { color: var(--dim); width: 7.5em; flex: none; }
  .meta dd { margin: 0; min-width: 0; overflow-wrap: anywhere; }
  .mono { font: 12.5px var(--mono); }
  .copy { cursor: copy; border-bottom: 1px dotted var(--faint); }
  .err { color: var(--failed); }

  /* The transcript. */
  .transcript { background: var(--card); border: 1px solid var(--line); border-radius: 10px; }
  .t-head { display: flex; align-items: center; gap: 14px; padding: 10px 16px; border-bottom: 1px solid var(--line); }
  .counts { font-weight: 600; } .counts span + span::before { content: "·"; margin: 0 7px; color: var(--faint); font-weight: 400; }
  .toggles { display: flex; gap: 2px; margin-left: auto; }
  .toggles button { display: inline-grid; place-items: center; width: 28px; height: 28px; border: 0; border-radius: 6px; background: transparent; color: var(--faint); padding: 0; }
  .toggles button:hover { background: var(--hover); color: var(--text); }
  .toggles button.on { color: var(--accent); }
  .t-body { padding: 6px 0 10px; }
  .empty, .following { color: var(--dim); font-size: 13px; }
  .empty { text-align: center; padding: 28px; }
  .following { padding: 8px 16px 4px 82px; }

  .e { display: grid; grid-template-columns: 64px 1fr; gap: 0 10px; padding: 5px 16px 5px 8px; }
  .e .t { font: 11.5px var(--mono); color: var(--faint); text-align: right; padding-top: 6px; }
  .e .t .took { display: block; font-variant-numeric: tabular-nums; }
  .e .c { min-width: 0; }

  .msg { display: grid; grid-template-columns: 16px 1fr; gap: 0 9px; border: 1px solid var(--line); border-radius: 8px; padding: 6px 10px; background: var(--card); min-width: 0; }
  .msg > svg.i { width: 15px; height: 15px; margin-top: 2px; color: var(--faint); }
  .msg > .body { min-width: 0; }
  .msg.prompt { background: var(--chip); border-color: transparent; } .msg.prompt > svg.i { color: var(--dim); }
  .msg.text > svg.i { color: var(--accent); }
  .msg.thinking { border-style: dashed; }
  .msg.thinking summary { cursor: pointer; color: var(--dim); font-style: italic; font-size: 13px; list-style: none; }
  .msg.thinking summary::before { content: "▸ "; font-style: normal; } .msg.thinking details[open] summary::before { content: "▾ "; }
  .msg .thought { color: var(--dim); font-style: italic; white-space: pre-wrap; margin-top: 4px; font-size: 13px; }
  .msg.notice { border-style: dashed; padding: 3px 10px; color: var(--faint); font: italic 13px system-ui, sans-serif; }
  .msg.answer { border-color: var(--completed); background: var(--completed-bg); } .msg.answer > svg.i { color: var(--completed); }
  .msg.answer.failed { border-color: var(--failed); background: var(--failed-bg); color: var(--failed); } .msg.answer.failed > svg.i { color: var(--failed); }
  .label { font-size: 11px; font-weight: 700; letter-spacing: .05em; text-transform: uppercase; color: var(--dim); margin-bottom: 2px; }

  .md { overflow-wrap: anywhere; }
  .md p { margin: 0 0 6px; } .md p:last-child { margin-bottom: 0; }
  .md ul { margin: 4px 0 6px; padding-left: 20px; }
  .md code { font: 12.5px var(--mono); background: var(--hover); border-radius: 4px; padding: 0 4px; overflow-wrap: anywhere; }
  .md pre.fence { font: 12.5px/1.5 var(--mono); background: var(--hover); border-radius: 6px; padding: 6px 10px; margin: 4px 0 8px; white-space: pre-wrap; overflow-wrap: anywhere; }
  .md a, .tool pre a { color: var(--accent); overflow-wrap: anywhere; }

  .tool details { border: 1px solid var(--line); border-radius: 8px; overflow: hidden; }
  .tool.error details { border-color: var(--failed); }
  .tool summary { display: block; padding: 5px 10px; cursor: pointer; list-style: none; font-size: 13px; min-width: 0; }
  .tool summary:hover { background: var(--hover); }
  .tool .row { display: flex; align-items: center; gap: 6px; min-width: 0; }
  .tool .row svg.i { width: 12px; height: 12px; color: var(--accent); }
  .tool .name { font: 600 12.5px var(--mono); color: var(--accent); flex: none; margin-right: 2px; }
  .tool .arg { font: 12.5px var(--mono); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; min-width: 0; flex: 1; }
  .tool .state { flex: none; font-size: 12px; color: var(--dim); }
  .tool .state.err { color: var(--failed); font-weight: 600; }
  .tool .state.running::before { content: ""; display: inline-block; width: 7px; height: 7px; border-radius: 50%; background: var(--working); margin-right: 5px; animation: pulse 1s infinite; }
  @keyframes pulse { 50% { opacity: .35; } }
  .tool .preview { display: block; font: 12px var(--mono); color: var(--dim); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; padding-left: 18px; margin-top: 1px; }
  .tool .preview::before { content: "↳ "; color: var(--faint); }
  .tool .preview.err { color: var(--failed); }
  .tool details[open] .preview { display: none; }
  .tool .sub { font-size: 11px; color: var(--faint); padding: 6px 10px 2px; border-top: 1px solid var(--line); text-transform: uppercase; letter-spacing: .05em; font-weight: 700; background: var(--bg); }
  .tool .sub.result::before { content: "↳ "; }
  .tool pre { margin: 0; padding: 8px 10px; font: 12px/1.5 var(--mono); white-space: pre-wrap; overflow-wrap: anywhere; background: var(--bg); max-height: 22em; overflow: auto; }
  .tool pre.result.err { color: var(--failed); }
  .tool .more { display: block; border: 0; background: var(--bg); color: var(--accent); font-size: 12px; padding: 2px 10px 8px; width: 100%; text-align: left; }
  .tool .input { background: var(--bg); padding: 4px 10px 8px; display: flex; flex-wrap: wrap; gap: 6px 14px; }
  .tool .kv { font: 12px var(--mono); } .tool .kv .k, .tool .field .k { color: var(--faint); } .tool .kv .v { overflow-wrap: anywhere; }
  .tool .field { flex-basis: 100%; min-width: 0; }
  .tool .field .k { font: 11px var(--mono); margin-bottom: 2px; }
  .tool .input pre { padding: 6px 8px; background: var(--card); border-radius: 6px; max-height: 26em; }
  .tool .input pre.shell::before { content: "$ "; color: var(--faint); }
  .tool .diff { flex-basis: 100%; font: 12px/1.5 var(--mono); background: var(--card); border-radius: 6px; padding: 4px 0; max-height: 26em; overflow: auto; }
  .tool .diff > div { white-space: pre-wrap; overflow-wrap: anywhere; padding: 0 8px; }
  .tool .diff .del { background: var(--failed-bg); color: var(--failed); } .tool .diff .add { background: var(--completed-bg); color: var(--completed); }

  .subagent { margin: 2px 0 2px 74px; border-left: 2px solid var(--line); }
  .subagent .e { grid-template-columns: 52px 1fr; padding-left: 4px; }
  .subagent-head { font-size: 12px; color: var(--dim); padding: 2px 12px; }
  .turn-end { display: flex; align-items: center; gap: 8px; padding: 8px 16px 8px 82px; color: var(--dim); font-size: 12.5px; }
  .turn-end::before, .turn-end::after { content: ""; flex: 1; border-top: 1px solid var(--line); }
  .turn-end.failed { color: var(--failed); }
  .turn-end .took { color: var(--faint); }

  /* The message box (M11), at the bottom of the transcript: the composer's look. */
  .composer { background: var(--card); border: 0; border-top: 1px solid var(--line); border-radius: 0 0 10px 10px; padding: 10px 16px 12px; }
  .composer textarea { display: block; width: 100%; min-height: 38px; max-height: 50vh; resize: none; overflow-y: hidden; border: 1px solid var(--line); border-radius: 7px; padding: 9px 11px; background: var(--field); color: var(--text); font: inherit; line-height: 20px; box-sizing: border-box; }
  .composer textarea.multiline { resize: vertical; overflow-y: auto; }
  .composer textarea:focus { outline: 2px solid var(--accent); outline-offset: -1px; border-color: transparent; }
  .composer .row { display: flex; align-items: center; gap: 8px 16px; margin-top: 10px; flex-wrap: wrap; }
  .spacer { flex: 1; }
  .hint { color: var(--faint); font-size: 12px; }
  .primary { height: 32px; border: 0; border-radius: 6px; padding: 0 16px; background: var(--accent-fill); color: var(--on-accent); font-weight: 600; }
  .primary:disabled { opacity: .45; cursor: default; }
  .send-problem { margin-top: 6px; font-size: 13px; color: var(--danger); }
  .no-agent { border-top: 1px solid var(--line); padding: 10px 16px; color: var(--dim); font-size: 13px; }
  .label .delivery { margin-left: 8px; font: 11px var(--mono); text-transform: none; letter-spacing: 0; font-weight: 400; color: var(--faint); }
  .label .delivery.continued { color: var(--working); }
  .msg.prompt.held { background: transparent; border: 1px dashed var(--line); }

  @media (max-width: 720px) {
    .e, .subagent .e { grid-template-columns: 1fr; padding: 4px 12px; }
    .e .t { text-align: left; padding-top: 0; }
    .subagent { margin-left: 12px; }
    .turn-end, .following { padding-left: 12px; }
    .t-head { flex-wrap: wrap; }
    .meta .wide { grid-column: auto; }
  }
`;
