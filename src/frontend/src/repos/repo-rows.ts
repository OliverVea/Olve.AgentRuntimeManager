import type { CreateSessionRepo } from "@arm/client";

/**
 * The checks a new session's repos get on the server (`RepoErrors.Check`), mirrored here so a bad
 * row shows on its field before anything is sent, as env's do; the server stays the judge (its
 * 400s show as the composer's other refusals). The folder name is worked out only to find two rows
 * that would share a folder: what a session's repos are checked out as (folder, branch) is read
 * from the API, never from here.
 */

/** .NET's `char.IsWhiteSpace`, which the server checks refs and trims URLs with. */
const space =
  "\\t\\n\\v\\f\\r \\u0085\\u00a0\\u1680\\u2000-\\u200a\\u2028\\u2029\\u202f\\u205f\\u3000";
/** .NET's `char.IsControl`. */
const control = "\\u0000-\\u001f\\u007f-\\u009f";
const edges = new RegExp(`^[${space}]+|[${space}]+$`, "g");
const badRefChar = new RegExp(`[${space}${control}]`);
const controlChar = new RegExp(`[${control}]`);

/** Trimmed as the server trims (`string.Trim`). */
const trim = (text: string) => text.replace(edges, "");

/** The URL's last segment without `.git`, as the server names a repo's folder; null if none is left. */
function folderOf(url: string): string | null {
  const trimmed = url.replace(/\/+$/, "");
  const cut = Math.max(
    trimmed.lastIndexOf("/"),
    trimmed.lastIndexOf(":"),
    trimmed.lastIndexOf("\\"),
  );
  let name = trimmed.slice(cut + 1);
  if (/\.git$/i.test(name)) name = name.slice(0, -4);
  return name === "" || name === "." || name === ".." || controlChar.test(name) ? null : name;
}

/** One row of the composer's repos, as typed: a clone URL and an optional ref. */
export type RepoRow = { url: string; ref: string };

/** A row with neither a URL nor a ref: ignored. */
export function isBlankRepoRow(row: RepoRow): boolean {
  return trim(row.url) === "" && trim(row.ref) === "";
}

export type RepoProblem = { part: "url" | "ref"; text: string };

export type RepoRowsCheck = {
  /** Per row, in order: its problem (and the field it's on), or undefined. */
  problems: (RepoProblem | undefined)[];
  /** The session's `repos` (trimmed; an empty ref left out); undefined while a row has a problem. */
  repos: CreateSessionRepo[] | undefined;
};

/**
 * Checks the composer's rows: a ref without a URL, a URL or ref git would take for an option, a
 * ref with spaces, a URL with no name to give its folder, and two that would share a folder (the
 * later one is flagged). Blank rows are ignored.
 */
export function checkRepoRows(rows: readonly RepoRow[]): RepoRowsCheck {
  const folders = new Set<string>();
  const repos: CreateSessionRepo[] = [];
  const problems = rows.map((row): RepoProblem | undefined => {
    if (isBlankRepoRow(row)) return undefined;
    const url = trim(row.url);
    const ref = trim(row.ref);
    if (!url) return { part: "url", text: "Give this ref a clone URL." };
    if (url.startsWith("-")) return { part: "url", text: "A clone URL can't start with -." };
    if (ref && (ref.startsWith("-") || badRefChar.test(ref)))
      return {
        part: "ref",
        text: "A ref is a branch, tag or commit: no spaces, not starting with -.",
      };
    const folder = folderOf(url);
    if (!folder)
      return { part: "url", text: "This URL has no repository name to name its folder after." };
    if (folders.has(folder))
      return { part: "url", text: `Another repo above would also go in the folder ${folder}.` };
    folders.add(folder);
    repos.push(ref ? { url, ref } : { url });
    return undefined;
  });
  return { problems, repos: problems.some(Boolean) ? undefined : repos };
}
