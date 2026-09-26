import { type ArmConfig, type ConfigKey, configKeys, configPath, isConfigKey, loadConfig, saveConfig } from "../config";
import { UsageError } from "../errors";
import { formatKeyValue } from "../output";
import type { CommandContext, CommandGroup } from "../registry";

const keyList = Object.keys(configKeys).join(", ");

function key(name: string): ConfigKey {
  if (!isConfigKey(name)) throw new UsageError(`unknown setting '${name}'. Settings: ${keyList}`);
  return name;
}

function dir(ctx: CommandContext): string {
  if (!ctx.configDir) throw new UsageError("no config folder: set HOME or ARM_HOME");
  return ctx.configDir;
}

function validate(name: ConfigKey, value: string): string {
  if (value === "") throw new UsageError(`${name} must not be empty (use 'arm config unset ${name}')`);
  if (name === "url") {
    let url: URL | undefined;
    try {
      url = new URL(value);
    } catch {
      url = undefined;
    }
    if (url?.protocol !== "http:" && url?.protocol !== "https:") {
      throw new UsageError(`invalid URL '${value}' (expected http:// or https://)`);
    }
  }
  return value;
}

export const configGroup: CommandGroup = {
  name: "config",
  summary: `Your defaults in ~/.arm/config.json (${keyList})`,
  defaultCommand: "list",
  commands: [
    {
      name: "list",
      summary: "Show every setting and where the file is",
      args: [],
      options: {},
      async run(ctx) {
        const config = loadConfig(ctx.configDir);
        const rows = Object.entries(configKeys).map(([name, spec]) => [name, config[name as ConfigKey] ?? `(not set; env ${spec.env})`] as const);
        const file = ctx.configDir ? configPath(ctx.configDir) : "(none: set HOME or ARM_HOME)";
        return { json: config, pretty: `${formatKeyValue(rows)}\n\nfile: ${file}` };
      },
    },
    {
      name: "get",
      summary: "Print one setting (empty when unset)",
      args: [{ name: "key", description: keyList }],
      options: {},
      async run(ctx) {
        const value = loadConfig(ctx.configDir)[key(ctx.args.key!)];
        return { json: value ?? null, pretty: value ?? "" };
      },
    },
    {
      name: "set",
      summary: "Save a setting",
      args: [
        { name: "key", description: keyList },
        { name: "value", description: "The value" },
      ],
      options: {},
      async run(ctx) {
        const name = key(ctx.args.key!);
        const folder = dir(ctx);
        const config: ArmConfig = { ...loadConfig(folder), [name]: validate(name, ctx.args.value!) };
        saveConfig(folder, config);
        return { json: config, pretty: `${name} = ${config[name]}` };
      },
    },
    {
      name: "unset",
      summary: "Remove a setting",
      args: [{ name: "key", description: keyList }],
      options: {},
      async run(ctx) {
        const name = key(ctx.args.key!);
        const folder = dir(ctx);
        const { [name]: _, ...config } = loadConfig(folder);
        saveConfig(folder, config);
        return { json: config, pretty: `${name} unset` };
      },
    },
  ],
};
