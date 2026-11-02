import "@typespec/http";

using Http;

namespace Arm;

/**
 * An external MCP server registered in ARM for agents (M10c), started as a stdio process. It's one
 * entry in `GET /api/tools`: a session names it in `tools`, and its agent gets all of the server's
 * tools (`github__create_issue`, shown as `github/create_issue` in conversations and activity).
 * Only sessions that name a server get it.
 *
 * Registering doesn't start the server, so a command that can't run shows when a session uses it:
 * a server that doesn't start within 120 s, or has a tool ARM can't give the agent, fails the
 * session. One that dies later has its calls return errors.
 *
 * Sessions take the registered definition when they're created: changing or deleting a server
 * later doesn't touch sessions that exist (their retries and continuations use the same one).
 *
 * `env` values aren't secret: anyone who can call the API reads them back, and an agent can read
 * them on the machine.
 */
model McpServer {
  /** What a session names in `tools`, e.g. `github`. */
  name: string;

  /** What it's for, in one line (`GET /api/tools`). */
  description: string;

  /** The program to start, looked up on the agent's `PATH`, e.g. `npx`. */
  command: string;

  /** Its arguments, e.g. `["-y", "pkg@1.2.3"]`. */
  args: string[];

  /** Variables it gets on top of the agent's environment. */
  env: Record<string>;

  updatedAt: utcDateTime;
}

model SetMcpServer {
  /** What it's for, in one line (`GET /api/tools`). */
  @minLength(1) description: string;

  /** The program to start, looked up on the agent's `PATH`, e.g. `npx`. */
  @minLength(1) command: string;

  /** Its arguments. None by default. */
  args?: string[];

  /**
   * Variables it gets on top of the agent's environment. Names ARM sets itself are reserved, as in
   * `EnvVariable` (a 400 `RESERVED_ENV_NAME`).
   */
  env?: Record<string>;
}

model McpServerName {
  /** `arm` and names starting with `arm-` are reserved (a 400 `RESERVED_MCP_SERVER_NAME`). */
  @path @pattern("^[a-z][a-z0-9-]*$") @maxLength(32) name: string;
}

@route("/mcp-servers")
interface McpServers {
  /** Every registered MCP server, by name. */
  @get list(): McpServer[] | UnauthorizedResponse;

  @get get(...McpServerName): McpServer | NotFound | UnauthorizedResponse;

  /** Registers an MCP server, or replaces it. */
  @put set(...McpServerName, @body body: SetMcpServer): McpServer | BadRequest | UnauthorizedResponse;

  @delete delete(...McpServerName): NoContentResponse | NotFound | UnauthorizedResponse;
}
