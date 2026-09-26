/** The CLI's own settings: `~/.arm/config.json` (or `$ARM_HOME/config.json`), managed by `arm config`. */
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { UsageError } from "./errors";

/** Every setting, its environment variable and what it's for. */
export const configKeys = {
  url: { env: "ARM_URL", description: "API base URL" },
  provider: { env: "ARM_PROVIDER", description: "Provider of new sessions" },
  model: { env: "ARM_MODEL", description: "Model of new sessions" },
  caller: { env: "ARM_CALLER", description: "Caller of new sessions and kills" },
} as const;

export type ConfigKey = keyof typeof configKeys;

export type ArmConfig = Partial<Record<ConfigKey, string>>;

export function isConfigKey(key: string): key is ConfigKey {
  return Object.hasOwn(configKeys, key);
}

/**
 * The folder: `$ARM_HOME`, else `$HOME/.arm`. Undefined when neither is set (tests), which means
 * no config at all.
 */
export function configDir(env: Record<string, string | undefined>): string | undefined {
  if (env.ARM_HOME) return env.ARM_HOME;
  return env.HOME ? join(env.HOME, ".arm") : undefined;
}

export function configPath(dir: string): string {
  return join(dir, "config.json");
}

/** The saved settings; a missing file is no settings, a malformed one a usage error. */
export function loadConfig(dir: string | undefined): ArmConfig {
  if (!dir) return {};
  let text: string;
  try {
    text = readFileSync(configPath(dir), "utf8");
  } catch {
    return {};
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    throw new UsageError(`${configPath(dir)} is not valid JSON; fix it or remove it`);
  }
  if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
    throw new UsageError(`${configPath(dir)} must hold a JSON object`);
  }
  return Object.fromEntries(
    Object.entries(parsed).filter(([key, value]) => isConfigKey(key) && typeof value === "string" && value !== ""),
  ) as ArmConfig;
}

export function saveConfig(dir: string, config: ArmConfig): void {
  mkdirSync(dir, { recursive: true });
  writeFileSync(configPath(dir), `${JSON.stringify(config, null, 2)}\n`);
}
