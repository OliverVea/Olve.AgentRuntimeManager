/** Bad invocation: unknown command/option, wrong arguments. Exit code 2. */
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

/** The API answered with a non-2xx status. Exit code 1. */
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

  /** The error as JSON: the body when there is one, else a SPEC-style error envelope. */
  toJSON(): unknown {
    if (isEmptyBody(this.body)) {
      return envelope(`HTTP_${this.status}`, this.message, { status: this.status });
    }
    return this.body;
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

  toJSON(): unknown {
    return envelope("NETWORK_ERROR", this.message, { url: this.url });
  }
}

export const ExitCode = {
  Ok: 0,
  Failure: 1,
  Usage: 2,
} as const;

function envelope(code: string, message: string, details: unknown) {
  return { error: { code, message, details } };
}

function isEmptyBody(body: unknown): boolean {
  if (body === undefined || body === null || body === "") return true;
  return typeof body === "object" && !Array.isArray(body) && Object.keys(body).length === 0;
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

/** Turns any of the error shapes the API returns into one readable line. */
export function describeApiError(status: number, statusText: string, body: unknown): string {
  const head = `${status} ${statusText || statusNames[status] || "Error"}`;
  const detail = extractDetail(body);
  return detail ? `${head}: ${detail}` : head;
}

function extractDetail(body: unknown): string | undefined {
  if (typeof body === "string") return body.trim() || undefined;
  // Current shape (Olve.MinimalApi): ResultProblem[]
  if (Array.isArray(body)) {
    const messages = body
      .map((p) => (p && typeof p === "object" && "message" in p ? String(p.message) : undefined))
      .filter((m): m is string => !!m);
    return messages.length ? messages.join("; ") : undefined;
  }
  if (body && typeof body === "object") {
    // SPEC envelope: { error: { code, message, details } }
    const error = (body as { error?: unknown }).error;
    if (error && typeof error === "object") {
      const { code, message } = error as { code?: unknown; message?: unknown };
      if (message) return code ? `${String(code)}: ${String(message)}` : String(message);
    }
    // RFC 7807 problem details
    const { title, detail } = body as { title?: unknown; detail?: unknown };
    if (detail || title) return String(detail ?? title);
  }
  return undefined;
}
