import {
  type McpServer,
  mcpServersDelete,
  mcpServersList,
  mcpServersSet,
  type SetMcpServer,
} from "@arm/client";
import type { Client } from "@arm/client/client";
import { describeError, unwrap } from "../api-errors.js";

/**
 * The MCP servers registered on the server (`GET /api/mcp-servers`, M10c), for the MCP servers
 * dialog. Fetched when the dialog opens and after every change; dispatches `change` whenever the
 * list or its problem does, and `written` after every change sent (failed or not), since each one
 * is also an entry in `GET /api/tools`.
 */
export class McpRegistry extends EventTarget {
  /** By name. */
  servers: McpServer[] = [];
  /** Whether a list has arrived yet. */
  loaded = false;
  /** Why the last fetch failed; empty when it didn't. */
  problem = "";
  #loads = 0;

  constructor(private readonly client: Client) {
    super();
  }

  get(name: string): McpServer | undefined {
    return this.servers.find((s) => s.name === name);
  }

  /** Fetches the list; a failure is kept in `problem` (the last list stays). */
  async load(): Promise<void> {
    const load = ++this.#loads;
    try {
      const servers = await unwrap(mcpServersList({ client: this.client }));
      if (load !== this.#loads) return; // a newer fetch answers
      this.servers = [...servers].sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0));
      this.loaded = true;
      this.problem = "";
    } catch (error) {
      if (load !== this.#loads) return;
      this.problem = describeError(error);
    }
    this.dispatchEvent(new Event("change"));
  }

  /** Registers a server, or replaces it whole (the API's PUT), then fetches the list; throws the API's error. */
  async set(name: string, body: SetMcpServer): Promise<void> {
    await this.#write(() => unwrap(mcpServersSet({ client: this.client, path: { name }, body })));
  }

  /** Deletes a server, then fetches the list; throws the API's error. */
  async remove(name: string): Promise<void> {
    await this.#write(() => unwrap(mcpServersDelete({ client: this.client, path: { name } })));
  }

  /** Forgets the list (signed out). */
  clear(): void {
    this.#loads++;
    this.servers = [];
    this.loaded = false;
    this.problem = "";
    this.dispatchEvent(new Event("change"));
  }

  async #write(send: () => Promise<unknown>): Promise<void> {
    try {
      await send();
    } finally {
      this.dispatchEvent(new Event("written"));
      await this.load();
    }
  }
}
