import { formatKeyValue } from "./output";
import { type Command, type CommandGroup, type OptionSpec, globalOptions } from "./registry";

function optionLines(options: Record<string, OptionSpec>): string {
  const entries = Object.entries(options).map(([name, spec]) => {
    const flag = `${spec.short ? `-${spec.short}, ` : "    "}--${name}${spec.type === "string" ? ` ${spec.valueName ?? "VALUE"}` : ""}`;
    return [flag, spec.multiple ? `${spec.description} (repeatable)` : spec.description] as const;
  });
  return indent(formatKeyValue(entries));
}

function indent(text: string): string {
  return text
    .split("\n")
    .map((l) => `  ${l}`)
    .join("\n");
}

const footer = `Environment:
  ARM_URL     API base URL (overridden by --url)
  ARM_TOKEN   Bearer token (overridden by --token)

Exit codes: 0 success, 1 other API error or network error, 2 usage error,
            3 not found (404), 4 conflict (409), 5 unavailable (503: queue full, draining).
Errors go to stderr; with --json the error body is printed there as JSON.`;

export function rootHelp(groups: readonly CommandGroup[], defaultUrl: string): string {
  return `arm — command-line client for Olve.AgentRuntimeManager

Usage:
  arm <group> <command> [arguments] [options]

Groups:
${indent(formatKeyValue(groups.map((g) => [g.name, g.summary] as const)))}

Global options:
${optionLines(globalOptions)}

Default URL: ${defaultUrl}

${footer}

Run 'arm <group> --help' for a group's commands.`;
}

function usageLine(group: CommandGroup, command: Command): string {
  const args = command.args.map((a) => `<${a.name}>`).join(" ");
  const name = group.defaultCommand === command.name ? "" : command.name;
  return ["arm", group.name, name, args, "[options]"].filter(Boolean).join(" ");
}

export function groupHelp(group: CommandGroup): string {
  return `${group.summary}

Usage:
${indent(group.commands.map((c) => usageLine(group, c)).join("\n"))}

Commands:
${indent(formatKeyValue(group.commands.map((c) => [c.name, c.summary] as const)))}

Run 'arm ${group.name} <command> --help' for details; 'arm --help' for global options.`;
}

export function commandHelp(group: CommandGroup, command: Command): string {
  const sections = [`${command.summary}\n\nUsage:\n  ${usageLine(group, command)}`];
  if (command.args.length) {
    sections.push(
      `Arguments:\n${indent(formatKeyValue(command.args.map((a) => [`<${a.name}>`, a.description] as const)))}`,
    );
  }
  if (Object.keys(command.options).length) {
    sections.push(`Options:\n${optionLines(command.options)}`);
  }
  sections.push(`Global options:\n${optionLines(globalOptions)}`);
  return sections.join("\n\n");
}
