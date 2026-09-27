import type { ConversationEntry } from "@arm/client";
import { describe, expect, it } from "vitest";
import {
  gist,
  inputFields,
  markdown,
  offset,
  preview,
  shellGist,
  took,
  topLevel,
  turnStart,
} from "./conversation.js";

const entry = (
  seq: number,
  kind: ConversationEntry["kind"],
  rest: Partial<ConversationEntry> = {},
): ConversationEntry => ({
  seq,
  kind,
  ...rest,
});

describe("conversation helpers", () => {
  it("markdown: fences, lists, bold, code and links, escaped", () => {
    const html = markdown(
      "**Done:** see `a<b>`\n\n- one\n- two\n\n```\nx < y\n```\n\nhttps://example.com/x.",
    );
    expect(html).toContain("<strong>Done:</strong>");
    expect(html).toContain("<code>a&lt;b&gt;</code>");
    expect(html).toContain("<ul><li>one</li><li>two</li></ul>");
    expect(html).toContain('<pre class="fence">x &lt; y</pre>');
    expect(html).toContain('<a href="https://example.com/x"');
  });

  it("shellGist drops cd/export/eval set-up", () => {
    expect(
      shellGist('cd /work && export PATH=$HOME/bin:$PATH; eval "$(mise env)"; mise run ci > log'),
    ).toBe("mise run ci > log");
    expect(shellGist("cd /work")).toBe("cd /work");
  });

  it("gist prefers a description, then the command's gist, a file, a pattern", () => {
    expect(gist(entry(1, "tool_call", { tool: "Bash", input: { command: "cd x && ls" } }))).toBe(
      "ls",
    );
    expect(
      gist(entry(1, "tool_call", { tool: "Bash", input: { command: "ls", description: "List" } })),
    ).toBe("List");
    expect(gist(entry(1, "tool_call", { tool: "Read", input: { file_path: "a.ts" } }))).toBe(
      "a.ts",
    );
  });

  it("preview: the first line with something in it, or an error's error line", () => {
    expect(preview(entry(2, "tool_result", { text: "\n  Switched to branch x\nmore" }))).toBe(
      "Switched to branch x",
    );
    expect(
      preview(entry(2, "tool_result", { isError: true, text: "remote: nope\nfatal: denied\n" })),
    ).toBe("fatal: denied");
    expect(preview(entry(2, "tool_result", { text: "" }))).toBe("(no output)");
  });

  it("took: tenths under 10s, then minutes and seconds", () => {
    expect(took("2026-09-27T15:00:00.000Z", "2026-09-27T15:00:00.050Z")).toBe("<0.1s");
    expect(took("2026-09-27T15:00:00.000Z", "2026-09-27T15:00:01.200Z")).toBe("1.2s");
    expect(took("2026-09-27T15:00:00Z", "2026-09-27T15:02:59Z")).toBe("2m 59s");
    expect(took("2026-09-27T15:00:00Z", "2026-09-27T15:00:16Z")).toBe("16s");
    expect(took(undefined, "2026-09-27T15:00:00Z")).toBe("");
  });

  it("offset: minutes and seconds into the conversation", () => {
    expect(offset("2026-09-27T15:01:45Z", "2026-09-27T15:00:00Z")).toBe("+1:45");
  });

  it("inputFields: short values inline, text whole, an Edit as a diff", () => {
    expect(
      inputFields(
        entry(1, "tool_call", { tool: "Bash", input: { command: "a\nb", timeout: 120000 } }),
      ),
    ).toEqual([
      { kind: "short", name: "timeout", value: "120000" },
      { kind: "text", name: "command", value: "a\nb", shell: true },
    ]);
    expect(
      inputFields(
        entry(1, "tool_call", {
          tool: "Edit",
          input: { file_path: "f", old_string: "x", new_string: "y" },
        }),
      ),
    ).toEqual([
      { kind: "short", name: "file_path", value: "f" },
      { kind: "diff", before: "x", after: "y" },
    ]);
  });

  it("topLevel leaves out results, subagent entries and text repeated as the answer", () => {
    const entries = [
      entry(1, "prompt", { text: "go" }),
      entry(2, "tool_call", { toolId: "t1" }),
      entry(3, "tool_result", { toolId: "t1" }),
      entry(4, "text", { text: "sub", parentToolId: "t1" }),
      entry(5, "text", { text: "Done." }),
      entry(6, "turn_end", { text: "Done." }),
    ];
    expect(topLevel(entries).map((e) => e.seq)).toEqual([1, 2, 6]);
    expect(turnStart(entries, entries[5]!)?.seq).toBe(1);
  });
});
