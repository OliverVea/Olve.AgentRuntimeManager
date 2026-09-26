/**
 * Tokens from `arm login`, per server URL, in `~/.arm/credentials.json` (0600; `$ARM_HOME` moves
 * it). Kept apart from config.json, which holds no secrets. The runner uses them when no
 * `--token`/`ARM_TOKEN` is given, refreshing an expired access token first.
 */
import { chmodSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import type { TokenSet } from "./auth/oidc";

export type Credential = TokenSet & {
  clientId: string;
  tokenEndpoint: string;
};

export type Credentials = Record<string, Credential>;

/** One key per server, however its URL was typed (trailing slashes). */
export const serverKey = (url: string): string => url.replace(/\/+$/, "");

export function credentialsPath(dir: string): string {
  return join(dir, "credentials.json");
}

export function loadCredentials(dir: string | undefined): Credentials {
  if (!dir) return {};
  try {
    const parsed = JSON.parse(readFileSync(credentialsPath(dir), "utf8")) as unknown;
    return parsed && typeof parsed === "object" && !Array.isArray(parsed) ? (parsed as Credentials) : {};
  } catch {
    return {};
  }
}

export function saveCredentials(dir: string, credentials: Credentials): void {
  mkdirSync(dir, { recursive: true });
  const path = credentialsPath(dir);
  writeFileSync(path, `${JSON.stringify(credentials, null, 2)}\n`, { mode: 0o600 });
  chmodSync(path, 0o600);
}

/** Whether the access token is expired, or expires within the next minute. */
export function isExpired(credential: TokenSet, now: Date): boolean {
  return credential.expiresAt !== undefined && Date.parse(credential.expiresAt) - now.getTime() < 60_000;
}
