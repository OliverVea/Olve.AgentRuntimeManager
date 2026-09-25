import { type Io, run } from "../src/cli";

export type Recorded = {
  method: string;
  url: string;
  headers: Headers;
  body: unknown;
};

type Reply = { status?: number; statusText?: string; body?: unknown } | Error;

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
      body: text ? JSON.parse(text) : undefined,
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

export type CliResult = { code: number; stdout: string; stderr: string };

export async function runCli(
  argv: string[],
  options: { env?: Record<string, string>; fetch?: typeof fetch } = {},
): Promise<CliResult> {
  const out: string[] = [];
  const err: string[] = [];
  const io: Io = {
    stdout: (t) => out.push(t),
    stderr: (t) => err.push(t),
    env: options.env ?? {},
    fetch:
      options.fetch ??
      (async () => {
        throw new Error("unexpected network call");
      }),
  };
  const code = await run(argv, io);
  return { code, stdout: out.join("\n"), stderr: err.join("\n") };
}
