import { parseArgs } from "node:util";
import pkg from "../package.json" with { type: "json" };
import { createApiClient } from "./api";
import { groups as defaultGroups } from "./commands";
import { ApiError, ExitCode, NetworkError, UsageError } from "./errors";
import { commandHelp, groupHelp, rootHelp } from "./help";
import { formatJson } from "./output";
import {
  type CommandGroup,
  type OptionSpec,
  type OptionValues,
  allOptions,
  findCommand,
  findGroup,
  globalOptions,
} from "./registry";

/** Where the backend listens by default in local dev (see backend HostConfiguration / appsettings.json). */
export const DEFAULT_URL = "http://localhost:5000";

export const VERSION: string = pkg.version;

export type Io = {
  stdout(text: string): void;
  stderr(text: string): void;
  env: Record<string, string | undefined>;
  /** Injected for tests; defaults to the global fetch. */
  fetch?: typeof fetch;
  /** Aborted on Ctrl+C (main.ts); streaming commands stop and exit 0. */
  signal?: AbortSignal;
  /** Called before a streaming command runs, so Ctrl+C aborts `signal` rather than the process. */
  onStreaming?(): void;
};

type ParseArgsOptions = Record<string, { type: "string" | "boolean"; short?: string }>;

function toParseArgs(options: Record<string, OptionSpec>): ParseArgsOptions {
  return Object.fromEntries(
    Object.entries(options).map(([name, spec]) => [
      name,
      {
        type: spec.type,
        ...(spec.short ? { short: spec.short } : {}),
      },
    ]),
  );
}

function suggestion(candidates: string[]): string {
  return `Available: ${candidates.join(", ")}.`;
}

/** Runs one CLI invocation and returns the process exit code. Never calls process.exit. */
export async function run(
  argv: readonly string[],
  io: Io,
  groups: readonly CommandGroup[] = defaultGroups,
): Promise<number> {
  // Pass 1 (lenient): knows every option of every command, so option values are never mistaken
  // for the group or verb. Only used to find the group/command and --help/--version/--json.
  const pre = parseArgs({
    args: [...argv],
    options: toParseArgs(allOptions(groups)),
    strict: false,
    allowPositionals: true,
  });
  const wantsJson = pre.values.json === true;
  const help = pre.values.help === true;
  const version = pre.values.version === true;

  const usage = (error: UsageError): number => {
    if (wantsJson) {
      io.stderr(formatJson({ error: { code: "USAGE", message: error.message, details: null } }));
    } else {
      io.stderr(`error: ${error.message}`);
      if (error.help) io.stderr(`\n${error.help}`);
    }
    return ExitCode.Usage;
  };

  const [groupName, commandName] = pre.positionals;

  if (!groupName) {
    if (help) return print(io, rootHelp(groups, DEFAULT_URL));
    if (version) return print(io, `arm ${VERSION}`);
    io.stderr(rootHelp(groups, DEFAULT_URL));
    return ExitCode.Usage;
  }

  const group = findGroup(groups, groupName);
  if (!group) {
    return usage(
      new UsageError(
        `unknown command group '${groupName}'. ${suggestion(groups.map((g) => g.name))}`,
      ),
    );
  }

  const defaultCommand = !commandName && group.defaultCommand ? findCommand(group, group.defaultCommand) : undefined;
  if (!commandName && !defaultCommand) {
    if (help) return print(io, groupHelp(group));
    if (version) return print(io, `arm ${VERSION}`);
    io.stderr(groupHelp(group));
    return ExitCode.Usage;
  }

  const command = defaultCommand ?? findCommand(group, commandName!);
  if (!command) {
    return usage(
      new UsageError(
        `unknown command '${group.name} ${commandName}'. ${suggestion(group.commands.map((c) => c.name))}`,
        `Run 'arm ${group.name} --help' for usage.`,
      ),
    );
  }
  if (help) return print(io, commandHelp(group, command));
  if (version) return print(io, `arm ${VERSION}`);

  const hint = `Run '${commandPath(group, command)} --help' for usage.`;

  // Pass 2 (strict): only the globals and this command's options are valid.
  let values: OptionValues;
  let positionals: string[];
  try {
    const parsed = parseArgs({
      args: [...argv],
      options: toParseArgs({ ...globalOptions, ...command.options }),
      strict: true,
      allowPositionals: true,
    });
    values = parsed.values as OptionValues;
    positionals = parsed.positionals.slice(defaultCommand ? 1 : 2);
  } catch (error) {
    return usage(new UsageError((error as Error).message, hint));
  }

  if (positionals.length !== command.args.length) {
    const expected = command.args.map((a) => `<${a.name}>`).join(" ") || "no arguments";
    const problem =
      positionals.length < command.args.length
        ? `missing argument <${command.args[positionals.length]!.name}>`
        : `unexpected argument '${positionals[command.args.length]}'`;
    return usage(new UsageError(`${problem} (expected ${expected})`, hint));
  }

  if (values.json === true && values.pretty === true) {
    return usage(new UsageError("--json and --pretty are mutually exclusive", hint));
  }

  const url = (values.url as string | undefined) || io.env.ARM_URL || DEFAULT_URL;
  if (!isHttpUrl(url)) {
    return usage(new UsageError(`invalid URL '${url}' (expected http:// or https://)`, hint));
  }
  const token = (values.token as string | undefined) || io.env.ARM_TOKEN || undefined;

  const client = createApiClient({ baseUrl: url, token, fetch: io.fetch });
  const args = Object.fromEntries(command.args.map((a, i) => [a.name, positionals[i]!]));
  const options = Object.fromEntries(Object.keys(command.options).map((name) => [name, values[name]]));

  const signal = io.signal ?? new AbortController().signal;
  if (command.streaming) io.onStreaming?.();

  try {
    const output = await command.run({
      args,
      options,
      client,
      json: wantsJson,
      stdout: io.stdout,
      stderr: io.stderr,
      signal,
    });
    if (!output) return ExitCode.Ok;
    return print(io, wantsJson ? formatJson(output.json) : output.pretty);
  } catch (error) {
    if (error instanceof UsageError) return usage(new UsageError(error.message, error.help ?? hint));
    if (error instanceof ApiError || error instanceof NetworkError) {
      io.stderr(wantsJson ? formatJson(error.toJSON()) : `error: ${error.message}`);
      return error.exitCode;
    }
    const message = error instanceof Error ? error.message : String(error);
    io.stderr(
      wantsJson
        ? formatJson({ error: { code: "UNEXPECTED", message, details: null } })
        : `error: unexpected failure: ${message}`,
    );
    return ExitCode.Failure;
  }
}

/** How the command is invoked: `arm session list`, or `arm events` for a group's default command. */
export function commandPath(group: CommandGroup, command: { name: string }): string {
  return group.defaultCommand === command.name ? `arm ${group.name}` : `arm ${group.name} ${command.name}`;
}

function print(io: Io, text: string): number {
  io.stdout(text);
  return ExitCode.Ok;
}

function isHttpUrl(value: string): boolean {
  try {
    const url = new URL(value);
    return url.protocol === "http:" || url.protocol === "https:";
  } catch {
    return false;
  }
}
