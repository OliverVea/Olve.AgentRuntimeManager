import type { McpServer, SetMcpServer } from "@arm/client";
import { envNameProblem } from "../env/env-names.js";

/**
 * An MCP server as the dialog's fields hold it (M10c), and the checks the server makes, done here
 * too so a problem shows on its field before anything is sent. Arguments are one per line (one can
 * hold spaces); env lines are `NAME=value`, split at the first `=`, the value as typed.
 */

/** The fields of a server, as typed. */
export type McpDraft = { description: string; command: string; args: string; env: string };

/** The fields a problem can be about. */
export type McpField = "name" | "description" | "command" | "args" | "env";

export const maxNameLength = 32;
const validName = /^[a-z][a-z0-9-]*$/;

/** What's wrong with a server's name, if anything (`McpServerName` in the contract). */
export function mcpNameProblem(name: string): string | undefined {
  if (name.length > maxNameLength || !validName.test(name))
    return `Names are lowercase letters, digits and -, starting with a letter, at most ${maxNameLength} characters.`;
  if (name === "arm" || name.startsWith("arm-"))
    return "arm and names starting with arm- are reserved for ARM.";
  return undefined;
}

/** The command line, an argument with spaces (or none at all) in quotes. */
export function commandLine(server: Pick<McpServer, "command" | "args">): string {
  return [
    server.command,
    ...server.args.map((a) => (a === "" || /\s/.test(a) ? `"${a}"` : a)),
  ].join(" ");
}

/** The list's line under a server: its command line, then its env's names (not their values). */
export function sourceText(server: Pick<McpServer, "command" | "args" | "env">): string {
  const names = Object.keys(server.env).map((n) => `${n}=…`);
  return [commandLine(server), ...(names.length ? [names.join(" ")] : [])].join("  ");
}

/** A registered server as the edit fields start. */
export function draftOf(server: McpServer): McpDraft {
  return {
    description: server.description,
    command: server.command,
    args: server.args.join("\n"),
    env: Object.entries(server.env)
      .map(([name, value]) => `${name}=${value}`)
      .join("\n"),
  };
}

const lines = (text: string) => text.split("\n").map((line) => line.replace(/\r$/, ""));

/** The arguments, one per line; blank lines are left out. */
export function parseArgs(text: string): string[] {
  return lines(text).filter((line) => line.trim() !== "");
}

/** The env lines as variables, or the first line's problem. Blank lines are left out. */
export function parseEnv(text: string): { env: Record<string, string> } | { problem: string } {
  // Entries, not assignments to `{}`: a valid name like `__proto__` must stay a key.
  const env: [string, string][] = [];
  const seen = new Set<string>();
  for (const line of lines(text)) {
    if (line.trim() === "") continue;
    const at = line.indexOf("=");
    if (at < 0) return { problem: "Env lines are NAME=value." };
    const name = line.slice(0, at).trim();
    const problem = envNameProblem(name, "given");
    if (problem) return { problem };
    if (seen.has(name)) return { problem: `${name} is given twice.` };
    seen.add(name);
    env.push([name, line.slice(at + 1)]);
  }
  return { env: Object.fromEntries(env) };
}

export type DraftCheck = {
  /** What can be sent; undefined while a field is missing or has a problem. */
  body: SetMcpServer | undefined;
  /** What's wrong, to show; empty when nothing is (a missing field shows none). */
  problem: string;
  /** The field the problem is about. */
  bad: McpField | undefined;
};

/**
 * Checks the fields: a description and a command are needed, and the env must parse. Editing a
 * registered server (`original`), a field whose text is as it started is sent as the server has
 * it: the fields can't hold everything the API can (an empty argument, one with a newline, a value
 * with a newline), and a save mustn't change what wasn't touched.
 */
export function checkDraft(draft: McpDraft, original?: McpServer): DraftCheck {
  const start = original && draftOf(original);
  const env = start?.env === draft.env ? { env: original?.env ?? {} } : parseEnv(draft.env);
  if ("problem" in env) return { body: undefined, problem: env.problem, bad: "env" };
  const description =
    start?.description === draft.description ? draft.description : draft.description.trim();
  const command = start?.command === draft.command ? draft.command : draft.command.trim();
  if (!description.trim() || !command.trim())
    return { body: undefined, problem: "", bad: undefined };
  const args = start?.args === draft.args ? (original?.args ?? []) : parseArgs(draft.args);
  return { body: { description, command, args, env: env.env }, problem: "", bad: undefined };
}
