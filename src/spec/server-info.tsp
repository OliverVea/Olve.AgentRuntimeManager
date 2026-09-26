import "@typespec/http";

using Http;

namespace Arm;

/** Which build this server runs and where. */
model ServerInfo {
  /** Build version, e.g. `2026.9.26.8`; `null` for a local build. */
  version: string | null;

  /** Deployment environment, e.g. `beta` or `prod`; `null` for a local run. */
  environment: string | null;
}

@route("/server-info")
interface ServerInfoApi {
  @get get(): ServerInfo;
}
