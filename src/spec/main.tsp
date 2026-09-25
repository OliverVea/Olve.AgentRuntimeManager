import "@typespec/http";

using Http;

/** ARM API contract. Source of truth — see docs/SPEC-FIRST.md. */
@service(#{ title: "Olve.AgentRuntimeManager" })
@route("/api")
namespace Arm;

/** A message — the scaffold's example entity, used to prove the toolchain (M1). */
model Message {
  @visibility(Lifecycle.Read) @format("uuid") id: string;
  @maxLength(280) text: string;
}

model Pagination {
  page?: int32;
  pageSize?: int32;
  offset?: int32;
}

model PageOfMessage {
  items: Message[];
  pageNumber: int32;
  pageSize: int32;
  totalCount: int32;
  totalPages?: int32;
  hasNextPage?: boolean;
  /** Null on the last page. */
  next?: Pagination | null;
}

/**
 * Current error shape (Olve.MinimalApi result mapping).
 * Replaced by the SPEC error envelope `{ error: { code, message, details } }` in M3 (A2).
 */
model ResultProblem {
  message: string;
  tags: string[] | null;
  severity: int32;
  source: string | null;
  exceptionSummary: string | null;
}

@error
model BadRequest {
  @statusCode status: 400;
  @body problems: ResultProblem[];
}

@error
model NotFound {
  @statusCode status: 404;
  @body problems: ResultProblem[];
}

/** Public OIDC settings the SPA needs to log in. */
model FrontendAuthConfig {
  authority: string | null;
  clientId: string | null;
  scopes: string;
}

@route("/messages")
interface Messages {
  /** List messages (1-based pages); out-of-range page/pageSize values are clamped. */
  @get list(@query page?: int32, @query pageSize?: int32): PageOfMessage;

  @post create(@body body: Message):
    | Message
    | BadRequest
    | UnauthorizedResponse;

  /** Get one message by id (anonymous, like `list`). */
  @get get(@path id: string): Message | NotFound;

  /**
   * A missing id is currently a 400 (Olve.MinimalApi maps every failed Result to 400), unlike
   * `get`'s 404; the SPEC error envelope in M3 settles not-found handling for all operations.
   */
  @put update(@path id: string, @body body: Message):
    | Message
    | BadRequest
    | UnauthorizedResponse;

  /** A missing id is currently a 400 — see `update`. */
  @delete delete(@path id: string): OkResponse | BadRequest | UnauthorizedResponse;
}

@route("/auth-config")
interface AuthConfig {
  @get get(): FrontendAuthConfig;
}
