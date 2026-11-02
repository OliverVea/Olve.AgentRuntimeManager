import "@typespec/http";

using Http;

namespace Arm;

/**
 * A tool ARM gives agents, by its own name (not the provider's), or a registered MCP server
 * (`GET /api/mcp-servers`), whose tools are one entry here.
 */
model Tool {
  /** What a session names in `tools`, e.g. `arm_bash` or `github`. */
  name: string;

  /** What it does, in one line. */
  description: string;

  /**
   * Whether a session that leaves `tools` out gets it (the server's configuration; by default
   * every one of ARM's). Never an MCP server.
   */
  default: boolean;
}

@route("/tools")
interface Tools {
  /** Every tool this server has, and every registered MCP server. */
  @get list(): Tool[] | UnauthorizedResponse;
}
