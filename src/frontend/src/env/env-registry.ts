import { type EnvVariable, envDelete, envList, envSet, type SetEnvVariable } from "@arm/client";
import type { Client } from "@arm/client/client";
import { describeError, unwrap } from "../api-errors.js";

/**
 * The environment variables registered on the server, shared by the composer's env panel and the
 * Environment variables dialog. Fetched on sign-in, when either opens, and after every change;
 * dispatches `change` whenever the list or its problem does.
 */
export class EnvRegistry extends EventTarget {
  /** By name. */
  variables: EnvVariable[] = [];
  /** Whether a list has arrived yet. */
  loaded = false;
  /** Why the last fetch failed; empty when it didn't. */
  problem = "";
  #loads = 0;

  constructor(private readonly client: Client) {
    super();
  }

  get defaults(): EnvVariable[] {
    return this.variables.filter((v) => v.default);
  }

  /** The ones a session picks by name (`useEnv`). */
  get optional(): EnvVariable[] {
    return this.variables.filter((v) => !v.default);
  }

  get(name: string): EnvVariable | undefined {
    return this.variables.find((v) => v.name === name);
  }

  /** Fetches the list; a failure is kept in `problem` (the last list stays). */
  async load(): Promise<void> {
    const load = ++this.#loads;
    try {
      const variables = await unwrap(envList({ client: this.client }));
      if (load !== this.#loads) return; // a newer fetch answers
      this.variables = [...variables].sort((a, b) =>
        a.name < b.name ? -1 : a.name > b.name ? 1 : 0,
      );
      this.loaded = true;
      this.problem = "";
    } catch (error) {
      if (load !== this.#loads) return;
      this.problem = describeError(error);
    }
    this.dispatchEvent(new Event("change"));
  }

  /** Registers or replaces a variable, then fetches the list; throws the API's error. */
  async set(name: string, body: SetEnvVariable): Promise<void> {
    try {
      await unwrap(envSet({ client: this.client, path: { name }, body }));
    } finally {
      await this.load();
    }
  }

  /** Deletes a variable, then fetches the list; throws the API's error. */
  async remove(name: string): Promise<void> {
    try {
      await unwrap(envDelete({ client: this.client, path: { name } }));
    } finally {
      await this.load();
    }
  }

  /** Forgets the list (signed out). */
  clear(): void {
    this.#loads++;
    this.variables = [];
    this.loaded = false;
    this.problem = "";
    this.dispatchEvent(new Event("change"));
  }
}
