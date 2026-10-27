import "@typespec/http";

using Http;

namespace Arm;

/** A tool ARM gives agents, by its own name (not the provider's). */
model Tool {
  /** What a session names in `tools`, e.g. `arm_bash`. */
  name: string;

  /** What it does, in one line. */
  description: string;

  /** Whether a session that leaves `tools` out gets it (the server's configuration). */
  default: boolean;
}

@route("/tools")
interface Tools {
  /** Every tool this server has. */
  @get list(): Tool[] | UnauthorizedResponse;
}
