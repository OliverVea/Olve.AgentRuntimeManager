import "@typespec/http";
import "./common.tsp";
import "./errors.tsp";
import "./auth-config.tsp";
import "./server-info.tsp";
import "./sessions.tsp";
import "./providers.tsp";
import "./events.tsp";

using Http;

/** ARM API contract. Source of truth — see docs/SPEC-FIRST.md. */
@service(#{ title: "Olve.AgentRuntimeManager" })
@useAuth(BearerAuth)
@route("/api")
namespace Arm;
