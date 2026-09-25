import type { Client } from "@arm/client/client";

/** A command-line option, in the shape node:util parseArgs understands plus help metadata. */
export type OptionSpec = {
  type: "string" | "boolean";
  short?: string;
  description: string;
  /** Placeholder shown in help for string options, e.g. `N` in `--page N`. */
  valueName?: string;
};

export type ArgSpec = {
  name: string;
  description: string;
};

export type OptionValues = Record<string, string | boolean | undefined>;

export type CommandContext = {
  /** Positional arguments by name (all required). */
  args: Record<string, string>;
  /** Command-specific option values (global options are handled by the runner). */
  options: OptionValues;
  /** Generated Hey API client, configured with base URL, token and fetch. */
  client: Client;
};

/** What a command produced: the raw API payload (`--json`) and its human-readable form (`--pretty`). */
export type CommandOutput = {
  json: unknown;
  pretty: string;
};

export type Command = {
  name: string;
  summary: string;
  args: ArgSpec[];
  options: Record<string, OptionSpec>;
  run(ctx: CommandContext): Promise<CommandOutput>;
};

export type CommandGroup = {
  name: string;
  summary: string;
  commands: Command[];
};

/** Options accepted by every command. */
export const globalOptions = {
  url: {
    type: "string",
    description: "API base URL (env ARM_URL)",
    valueName: "URL",
  },
  token: {
    type: "string",
    description: "Bearer token (env ARM_TOKEN)",
    valueName: "TOKEN",
  },
  json: { type: "boolean", description: "Print raw JSON" },
  pretty: {
    type: "boolean",
    description: "Print human-readable output (default)",
  },
  help: { type: "boolean", short: "h", description: "Show help" },
  version: { type: "boolean", short: "V", description: "Show version" },
} as const satisfies Record<string, OptionSpec>;

export function findGroup(
  groups: readonly CommandGroup[],
  name: string,
): CommandGroup | undefined {
  return groups.find((g) => g.name === name);
}

export function findCommand(
  group: CommandGroup,
  name: string,
): Command | undefined {
  return group.commands.find((c) => c.name === name);
}

/**
 * Every option any command declares, plus the globals. Used for a lenient first pass that finds
 * the group and verb without knowing which command's options apply yet. Throws if two commands
 * declare the same option name with different types (that would make pre-scanning ambiguous).
 */
export function allOptions(
  groups: readonly CommandGroup[],
): Record<string, OptionSpec> {
  const all: Record<string, OptionSpec> = { ...globalOptions };
  for (const group of groups) {
    for (const command of group.commands) {
      for (const [name, spec] of Object.entries(command.options)) {
        const existing = all[name];
        if (existing && (existing.type !== spec.type || existing.short !== spec.short)) {
          throw new Error(
            `Option --${name} (${group.name} ${command.name}) conflicts with another declaration`,
          );
        }
        all[name] = spec;
      }
    }
  }
  return all;
}
