import { type Io, run } from "../src/cli";

export type Recorded = {
  method: string;
  url: string;
  headers: Headers;
  body: unknown;
};

type Reply = { status?: number; statusText?: string; body?: unknown } | Error;

/** JSON bodies parsed; anything else (form-encoded token requests) kept as text. */
function parseBody(text: string): unknown {
  try {
    return JSON.parse(text);
  } catch {
    return text;
  }
}

/** A fetch stand-in that records each request and answers with `reply(request)`. */
export function fakeFetch(reply: (req: Recorded) => Reply) {
  const requests: Recorded[] = [];
  const fn = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const request = input instanceof Request ? input : new Request(input, init);
    const text = await request.text();
    const recorded: Recorded = {
      method: request.method,
      url: request.url,
      headers: request.headers,
      body: text ? parseBody(text) : undefined,
    };
    requests.push(recorded);
    const r = reply(recorded);
    if (r instanceof Error) throw r;
    const status = r.status ?? 200;
    const body = r.body === undefined ? null : typeof r.body === "string" ? r.body : JSON.stringify(r.body);
    return new Response(body, {
      status,
      statusText: r.statusText ?? "",
      headers: body && typeof r.body !== "string" ? { "Content-Type": "application/json" } : {},
    });
  };
  return { fetch: fn as typeof fetch, requests };
}

/** One scripted answer of {@link sseFetch}: a status with a JSON body, or an SSE stream. */
export type SseReply =
  | { status: number; body?: unknown }
  | {
      /** Raw SSE frames, each written as one chunk (end each with a blank line). */
      frames: string[];
      /** After the frames: `close` ends the stream; `hang` keeps it open until the client aborts. */
      then: "close" | "hang";
    }
  /** Never answers: the fetch rejects only when its request is aborted (a hung connect). */
  | "pending"
  | Error;

export type SseRequest = { url: string; headers: Headers; signal: AbortSignal };

/** A fetch stand-in for SSE: answers the n-th request with `replies[n]` (the last one repeats). */
export function sseFetch(replies: SseReply[]) {
  const requests: SseRequest[] = [];
  const fn = async (input: RequestInfo | URL, init?: RequestInit): Promise<Response> => {
    const request = input instanceof Request ? input : new Request(input, init);
    requests.push({ url: request.url, headers: request.headers, signal: request.signal });
    const reply = replies[Math.min(requests.length - 1, replies.length - 1)]!;
    if (reply instanceof Error) throw reply;
    if (reply === "pending") {
      return new Promise<Response>((_, reject) =>
        request.signal.addEventListener("abort", () => reject(new DOMException("aborted", "AbortError"))),
      );
    }
    if ("status" in reply) {
      return new Response(reply.body === undefined ? null : JSON.stringify(reply.body), {
        status: reply.status,
        headers: reply.body === undefined ? {} : { "Content-Type": "application/json" },
      });
    }
    const encoder = new TextEncoder();
    const body = new ReadableStream<Uint8Array>({
      start(controller) {
        for (const frame of reply.frames) controller.enqueue(encoder.encode(frame));
        if (reply.then === "close") controller.close();
        else
          request.signal.addEventListener("abort", () => {
            try {
              controller.error(new DOMException("aborted", "AbortError"));
            } catch {
              // already cancelled by the reader
            }
          });
      },
    });
    return new Response(body, { status: 200, headers: { "Content-Type": "text/event-stream" } });
  };
  return { fetch: fn as typeof fetch, requests };
}

/** An SSE frame: `event`, optional `id`, and the JSON data. */
export function frame(event: string, data: unknown, id?: string): string {
  return `event: ${event}\n${id ? `id: ${id}\n` : ""}data: ${JSON.stringify(data)}\n\n`;
}

export type CliResult = { code: number; stdout: string; stderr: string };

export async function runCli(
  argv: string[],
  options: {
    env?: Record<string, string>;
    fetch?: typeof fetch;
    signal?: AbortSignal;
    /** Called with every stdout line so far, after each write (e.g. to Ctrl+C after N events). */
    onStdout?: (lines: string[]) => void;
    openBrowser?: (url: string) => void;
    now?: () => Date;
  } = {},
): Promise<CliResult> {
  const out: string[] = [];
  const err: string[] = [];
  const io: Io = {
    stdout: (t) => {
      out.push(t);
      options.onStdout?.(out);
    },
    stderr: (t) => err.push(t),
    signal: options.signal,
    env: options.env ?? {},
    openBrowser: options.openBrowser,
    now: options.now,
    sleep: async () => {},
    fetch:
      options.fetch ??
      (async () => {
        throw new Error("unexpected network call");
      }),
  };
  const code = await run(argv, io);
  return { code, stdout: out.join("\n"), stderr: err.join("\n") };
}
