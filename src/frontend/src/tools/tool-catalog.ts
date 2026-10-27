import { type Tool, toolsList } from "@arm/client";
import type { Client } from "@arm/client/client";
import { describeError, unwrap } from "../api-errors.js";

/**
 * ARM's tools (`GET /api/tools`, M10): what the composer's tools panel lists, and which the server
 * gives a session that names none (`default`). Fetched on sign-in and after a create is refused
 * with `UNKNOWN_TOOL`; dispatches `change` whenever the list or its problem does.
 */
export class ToolCatalog extends EventTarget {
  tools: Tool[] = [];
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
      const tools = await unwrap(toolsList({ client: this.client }));
      if (load !== this.#loads) return; // a newer fetch answers
      this.tools = tools;
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
    this.tools = [];
    this.loaded = false;
    this.problem = "";
    this.dispatchEvent(new Event("change"));
  }
}
