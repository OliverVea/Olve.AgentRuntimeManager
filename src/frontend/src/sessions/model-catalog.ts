import { type Provider, providersList } from "@arm/client";
import type { Client } from "@arm/client/client";
import { describeError, unwrap } from "../api-errors.js";

/**
 * The providers this server has and the models they offer (`GET /api/providers`, M4b): what the
 * composer's model dropdown lists and where cards find display names. Fetched on sign-in, when
 * Options opens while it isn't loaded, and after a create is refused with `UNKNOWN_MODEL`;
 * dispatches `change` whenever the list or its problem does.
 */
export class ModelCatalog extends EventTarget {
  providers: Provider[] = [];
  /** Whether a list has arrived yet. */
  loaded = false;
  /** Why the last fetch failed; empty when it didn't. */
  problem = "";
  #loads = 0;

  constructor(private readonly client: Client) {
    super();
  }

  /** Fetches the list; a failure is kept in `problem` (the last list stays). */
  async load(): Promise<void> {
    const load = ++this.#loads;
    try {
      const providers = await unwrap(providersList({ client: this.client }));
      if (load !== this.#loads) return; // a newer fetch answers
      this.providers = providers;
      this.loaded = true;
      this.problem = "";
    } catch (error) {
      if (load !== this.#loads) return;
      this.problem = describeError(error);
    }
    this.dispatchEvent(new Event("change"));
  }

  /** Forgets the list (signed out). */
  clear(): void {
    this.#loads++;
    this.providers = [];
    this.loaded = false;
    this.problem = "";
    this.dispatchEvent(new Event("change"));
  }
}

/** A dropdown entry: `provider/model` as the value, `Opus 5.5 (Claude Code)` as the label. */
export type ModelOption = { value: string; label: string };

/** What a dropdown lists and which entry it selects. */
export type ModelChoice = { options: ModelOption[]; selected: string };

/** `provider/model`: the dropdown's value, and what prefs keep as the default model. */
export function modelValue(provider: string, model: string): string {
  return `${provider}/${model}`;
}

/**
 * The dropdown for these providers, selecting `saved` when it's offered and else the first model
 * (a default the server no longer offers, like an old `claude/sonnet`). With no list
 * (`undefined`: still loading, or the read failed) it holds only `saved`, by its id, so the
 * composer still works; the server refuses it if it's no longer offered.
 */
export function modelChoice(
  providers: readonly Provider[] | undefined,
  saved: string,
): ModelChoice {
  if (!providers) {
    const slash = saved.indexOf("/");
    const label = slash > 0 ? `${saved.slice(slash + 1)} (${saved.slice(0, slash)})` : saved;
    return { options: saved ? [{ value: saved, label }] : [], selected: saved };
  }
  const options = providers.flatMap((p) =>
    p.models.map((m) => ({
      value: modelValue(p.name, m.id),
      label: `${m.displayName} (${p.displayName})`,
    })),
  );
  const selected = options.some((o) => o.value === saved) ? saved : (options[0]?.value ?? "");
  return { options, selected };
}
