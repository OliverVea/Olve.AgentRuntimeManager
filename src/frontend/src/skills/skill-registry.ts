import {
  type InstallSkill,
  type Skill,
  skillsDelete,
  skillsInstall,
  skillsList,
} from "@arm/client";
import type { Client } from "@arm/client/client";
import { describeError, unwrap } from "../api-errors.js";

/**
 * The skills registered on the server (`GET /api/skills`, M10b), shared by the composer's skills
 * panel and the Skills dialog. Fetched on sign-in, when the dialog opens, and after every change;
 * dispatches `change` whenever the list or its problem does.
 */
export class SkillRegistry extends EventTarget {
  /** By name. */
  skills: Skill[] = [];
  /** Whether a list has arrived yet. */
  loaded = false;
  /** Why the last fetch failed; empty when it didn't. */
  problem = "";
  #loads = 0;

  constructor(private readonly client: Client) {
    super();
  }

  /** The ones every session that leaves `skills` unset gets. */
  get defaults(): Skill[] {
    return this.skills.filter((s) => s.default);
  }

  get(name: string): Skill | undefined {
    return this.skills.find((s) => s.name === name);
  }

  /** Fetches the list; a failure is kept in `problem` (the last list stays). */
  async load(): Promise<void> {
    const load = ++this.#loads;
    try {
      const skills = await unwrap(skillsList({ client: this.client }));
      if (load !== this.#loads) return; // a newer fetch answers
      this.skills = [...skills].sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
      this.loaded = true;
      this.problem = "";
    } catch (error) {
      if (load !== this.#loads) return;
      this.problem = describeError(error);
    }
    this.dispatchEvent(new Event("change"));
  }

  /** Installs a skill, or replaces it (the API's PUT), then fetches the list; throws the API's error. */
  async install(name: string, body: InstallSkill): Promise<void> {
    try {
      await unwrap(skillsInstall({ client: this.client, path: { name }, body }));
    } finally {
      await this.load();
    }
  }

  /** Flags a skill `default` or not: installs it again from the same source. */
  async setDefault(name: string, isDefault: boolean): Promise<void> {
    const skill = this.get(name);
    if (!skill) return;
    await this.install(name, { source: skill.source, default: isDefault });
  }

  /** Deletes a skill, then fetches the list; throws the API's error. */
  async remove(name: string): Promise<void> {
    try {
      await unwrap(skillsDelete({ client: this.client, path: { name } }));
    } finally {
      await this.load();
    }
  }

  /** Forgets the list (signed out). */
  clear(): void {
    this.#loads++;
    this.skills = [];
    this.loaded = false;
    this.problem = "";
    this.dispatchEvent(new Event("change"));
  }
}
