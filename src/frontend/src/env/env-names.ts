/**
 * The names environment variables may have, as the server checks them (`EnvNames`, `EnvErrors`):
 * letters, digits and `_`, not starting with a digit, and none that ARM sets for its agents
 * itself. Case-sensitive, like the server. Checked here too so a bad name shows on its field
 * before anything is sent.
 */

const validName = /^[A-Za-z_][A-Za-z0-9_]*$/;

/** The names ARM sets for its agents itself (the lockdown, the login). */
export const reservedEnvNames: readonly string[] = [
  "PATH",
  "HOME",
  "USER",
  "LANG",
  "LC_ALL",
  "TERM",
  "TMPDIR",
  "DISABLE_UPDATES",
  "ENABLE_CLAUDEAI_MCP_SERVERS",
];

/** Every name starting with one of these is reserved too. */
export const reservedEnvPrefixes: readonly string[] = ["CLAUDE_", "ARM_"];

export function isValidEnvName(name: string): boolean {
  return validName.test(name);
}

export function isReservedEnvName(name: string): boolean {
  return reservedEnvNames.includes(name) || reservedEnvPrefixes.some((p) => name.startsWith(p));
}

/**
 * What's wrong with a name, if anything: `given` for a session's own env, `registered` for the
 * list of registered variables (the last word of the reserved message).
 */
export function envNameProblem(name: string, use: "given" | "registered"): string | undefined {
  if (!isValidEnvName(name)) return "Names are letters, digits and _, not starting with a digit.";
  if (isReservedEnvName(name)) return `${name} is set by ARM for its agents and can't be ${use}.`;
  return undefined;
}

/** One NAME = VALUE row of the composer, as typed. */
export type EnvRow = { name: string; value: string };

/** A row with neither a name nor a value: ignored. */
export function isBlankRow(row: EnvRow): boolean {
  return row.name.trim() === "" && row.value.trim() === "";
}

export type EnvRowsCheck = {
  /** Per row, in order: its problem, or undefined. */
  problems: (string | undefined)[];
  /** The session's `env` (names trimmed, values as typed); undefined while a row has a problem. */
  env: Record<string, string> | undefined;
};

/**
 * Checks the composer's rows: every name as for `given`, a name used twice (the second and later
 * rows are flagged), a value without a name. Blank rows are ignored.
 */
export function checkEnvRows(rows: readonly EnvRow[]): EnvRowsCheck {
  const seen = new Set<string>();
  const env: Record<string, string> = {};
  const problems = rows.map((row) => {
    if (isBlankRow(row)) return undefined;
    const name = row.name.trim();
    if (!name) return "Give this value a name.";
    const problem = envNameProblem(name, "given");
    if (problem) return problem;
    if (seen.has(name)) return `${name} is given twice.`;
    seen.add(name);
    env[name] = row.value;
    return undefined;
  });
  return { problems, env: problems.some(Boolean) ? undefined : env };
}
