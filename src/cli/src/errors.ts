/** Bad invocation: unknown command/option, wrong arguments, malformed option values. Exit code 2. */
export class UsageError extends Error {
  constructor(
    message: string,
    /** Help text to print after the error, if any. */
    readonly help?: string,
  ) {
    super(message);
    this.name = "UsageError";
  }
}

/** The API answered with a non-2xx status. Exit code by status: see {@link exitCodeForStatus}. */
export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly statusText: string,
    /** Parsed response body (`{}` when the body was empty). */
    readonly body: unknown,
  ) {
    super(describeApiError(status, statusText, body));
    this.name = "ApiError";
  }

  get exitCode(): number {
    return exitCodeForStatus(this.status);
  }

  /** The error as JSON: the API's error envelope, or one built from the status when there is none. */
  toJSON(): unknown {
    if (errorEnvelope(this.body)) return this.body;
    return envelope(`HTTP_${this.status}`, this.message, { status: this.status });
  }
}

/** The request never got a response (connection refused, DNS, TLS, ...). Exit code 1. */
export class NetworkError extends Error {
  constructor(
    readonly url: string,
    readonly reason: unknown,
  ) {
    super(`could not reach ${url}: ${causeMessage(reason)}`);
    this.name = "NetworkError";
  }

  readonly exitCode: number = ExitCode.Failure;

  toJSON(): unknown {
    return envelope("NETWORK_ERROR", this.message, { url: this.url });
  }
}

export const ExitCode = {
  Ok: 0,
  /** Any other API error (400, 401, 5xx, ...), a network error, or an unexpected failure. */
  Failure: 1,
  Usage: 2,
  NotFound: 3,
  Conflict: 4,
  /** 503: the session queue is full, or the server is draining. */
  Unavailable: 5,
} as const;

/** 404 → 3, 409 → 4, 503 → 5; every other error status → 1. */
export function exitCodeForStatus(status: number): number {
  switch (status) {
    case 404:
      return ExitCode.NotFound;
    case 409:
      return ExitCode.Conflict;
    case 503:
      return ExitCode.Unavailable;
    default:
      return ExitCode.Failure;
  }
}

function envelope(code: string, message: string, details: unknown) {
  return { error: { code, message, details } };
}

type Problem = { code?: unknown; message?: unknown };
type EnvelopeError = { code?: unknown; message?: unknown; details?: { problems?: unknown } };

/** The `error` of the API's error envelope `{ error: { code, message, details } }`, if `body` is one. */
function errorEnvelope(body: unknown): EnvelopeError | undefined {
  if (!body || typeof body !== "object" || Array.isArray(body)) return undefined;
  const error = (body as { error?: unknown }).error;
  if (!error || typeof error !== "object" || Array.isArray(error)) return undefined;
  const { code, message } = error as EnvelopeError;
  return typeof code === "string" || typeof message === "string" ? (error as EnvelopeError) : undefined;
}

function causeMessage(cause: unknown): string {
  if (cause instanceof Error) {
    const inner = (cause as { cause?: unknown }).cause;
    return inner instanceof Error ? `${cause.message} (${inner.message})` : cause.message;
  }
  return String(cause);
}

const statusNames: Record<number, string> = {
  400: "Bad Request",
  401: "Unauthorized",
  403: "Forbidden",
  404: "Not Found",
  409: "Conflict",
  410: "Gone",
  422: "Unprocessable Entity",
  429: "Too Many Requests",
  500: "Internal Server Error",
  502: "Bad Gateway",
  503: "Service Unavailable",
};

/**
 * Describes an error response: `404 Not Found: SESSION_NOT_FOUND: …` from the error envelope, then
 * one `  - CODE: message` line per problem when there are several. Any other body (none, or e.g.
 * an HTML page from the edge) gives just the status.
 */
export function describeApiError(status: number, statusText: string, body: unknown): string {
  const head = `${status} ${statusText || statusNames[status] || "Error"}`;
  const error = errorEnvelope(body);
  if (!error) return head;
  const summary = [error.code, error.message].filter((p) => typeof p === "string" && p).join(": ");
  const lines = [summary ? `${head}: ${summary}` : head];
  const problems = Array.isArray(error.details?.problems) ? (error.details.problems as Problem[]) : [];
  if (problems.length > 1) {
    for (const p of problems) {
      const text = [p?.code, p?.message].filter((part) => typeof part === "string" && part).join(": ");
      if (text) lines.push(`  - ${text}`);
    }
  }
  return lines.join("\n");
}
