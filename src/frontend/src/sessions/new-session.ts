import type { CreateSession } from "@arm/client";
import { checkEnvRows, type EnvRow } from "../env/env-names.js";
import { checkRepoRows, type RepoRow } from "../repos/repo-rows.js";
import { parseModel } from "./format.js";

/** What the composer's fields hold, as typed. */
export type NewSessionFields = {
  prompt: string;
  /** The dropdown's `provider/model`. */
  model: string;
  caller: string;
  timeout: string;
  /** The session's own NAME = VALUE rows; blank ones are ignored. */
  env?: readonly EnvRow[];
  /** The registered, non-default variables picked for it. */
  useEnv?: readonly string[];
  /** The session's repos: a clone URL and an optional ref each; blank rows are ignored. */
  repos?: readonly RepoRow[];
};

export type Parsed<T> = { ok: true; value: T } | { ok: false; problem: string };

/** A timeout field: empty is no timeout, else a whole number of seconds from 1. */
export function parseTimeout(text: string): Parsed<number | undefined> {
  const trimmed = text.trim();
  if (!trimmed) return { ok: true, value: undefined };
  if (!/^\d+$/.test(trimmed) || Number(trimmed) < 1) {
    return {
      ok: false,
      problem: "The timeout must be a whole number of seconds (or empty for none).",
    };
  }
  return { ok: true, value: Number(trimmed) };
}

/** The create request for the composer's fields; `fallbackCaller` fills an empty caller. */
export function newSession(
  fields: NewSessionFields,
  fallbackCaller: string,
): Parsed<CreateSession> {
  const prompt = fields.prompt.trim();
  if (!prompt) return { ok: false, problem: "Say what the agent should do." };
  const model = parseModel(fields.model);
  if (!model) return { ok: false, problem: "Pick a model." };
  const timeout = parseTimeout(fields.timeout);
  if (!timeout.ok) return timeout;
  const env = checkEnvRows(fields.env ?? []);
  if (!env.env) return { ok: false, problem: env.problems.find(Boolean) ?? "Check the env." };
  const repos = checkRepoRows(fields.repos ?? []);
  if (!repos.repos)
    return { ok: false, problem: repos.problems.find(Boolean)?.text ?? "Check the repos." };
  const caller = fields.caller.trim() || fallbackCaller;
  const body: CreateSession = { prompt, ...model, caller };
  if (timeout.value !== undefined) body.timeoutSeconds = timeout.value;
  if (Object.keys(env.env).length) body.env = env.env;
  if (fields.useEnv?.length) body.useEnv = [...fields.useEnv];
  if (repos.repos.length) body.repos = repos.repos;
  return { ok: true, value: body };
}
