import "@typespec/http";

using Http;

namespace Arm;

/**
 * An environment variable registered in ARM for agents. A `default` one goes to every agent;
 * the others only to sessions that name them (`CreateSession.useEnv`). A session's own `env`
 * wins on a shared name. Sessions take the registered values when they're created: changing or
 * deleting a variable later doesn't touch sessions that exist (their retries and resumes get
 * the same environment).
 *
 * Names ARM sets for its agents itself are reserved (a 400 `RESERVED_ENV_NAME`, here and in a
 * session's `env`): `PATH`, `HOME`, `USER`, `LANG`, `LC_ALL`, `TERM`, `TMPDIR`,
 * `DISABLE_UPDATES`, `ENABLE_CLAUDEAI_MCP_SERVERS`, and every name starting with `CLAUDE_` or
 * `ARM_`.
 *
 * Values aren't secret: anyone who can call the API reads them back.
 */
model EnvVariable {
  /** The variable's name, e.g. `GIT_AUTHOR_NAME`. */
  name: string;

  value: string;

  /** Whether every agent gets it. */
  `default`: boolean;

  updatedAt: utcDateTime;
}

model SetEnvVariable {
  value: string;

  /** Whether every agent gets it. */
  `default`: boolean;
}

model EnvVariableName {
  @path @pattern("^[A-Za-z_][A-Za-z0-9_]*$") name: string;
}

@route("/env")
interface Env {
  /** Every registered environment variable, by name. */
  @get list(): EnvVariable[] | UnauthorizedResponse;

  /** Registers an environment variable, or replaces it. */
  @put set(...EnvVariableName, @body body: SetEnvVariable): EnvVariable | BadRequest | UnauthorizedResponse;

  @delete delete(...EnvVariableName): NoContentResponse | BadRequest | NotFound | UnauthorizedResponse;
}
