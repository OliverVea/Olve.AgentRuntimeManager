# arm — CLI

Command-line client for Olve.AgentRuntimeManager. It is hand-written on the Hey API TypeScript
client that `npm run generate` (repo root) produces in `artifacts/clients/ts`, and it is compiled to a
single self-contained binary with `bun build --compile`.

## Usage

```
arm <group> <command> [arguments] [options]

arm session create <prompt> [--provider X] [--model X] [--caller X] [--timeout-seconds N]
                   [--idempotency-key KEY]
arm session list [--status X,Y] [--caller X] [--after DATE] [--before DATE] [--limit N] [--offset N]
arm session get <id>
arm session kill <id> [--caller X] [--reason TEXT]
arm session delete <id>

arm events [--event X,Y] [--exclude-event X,Y] [--last-event-id ID]

arm server [info]

arm login [--device | --browser] | arm logout
arm config [list] | arm config get <key> | arm config set <key> <value> | arm config unset <key>

arm --help | arm <group> --help | arm <group> <command> --help
arm --version
```

Global options (any position):

| Option | Env | Default | |
|---|---|---|---|
| `--url URL` | `ARM_URL` | `http://localhost:5000` | API base URL (the backend's local dev port) |
| `--token TOKEN` | `ARM_TOKEN` | none | Sent as `Authorization: Bearer …` on every authenticated operation (all but auth-config) |
| `--json` | | | Print the raw API JSON |
| `--pretty` | | on | Human-readable: a table for lists, key/value for objects |

Precedence is flag, then env, then `~/.arm/config.json` (see [Defaults](#defaults)), then the
built-in default. A prompt starting with a dash goes after `--`:
`arm session create --caller me -- "-5 degrees"`. `--after`/`--before` take a date or date-time
(`2026-09-01`, `2026-09-01T12:00:00Z`); a bare date is midnight UTC. An empty option value, a
missing or blank `<prompt>`, a `caller` found nowhere (flag, env, config, OS user) and malformed
values (an unknown `--status`, `--limit` outside 1–100, …) are usage errors before any request is
sent.

## Logging in

`arm login` works like `pl login`: it asks the server which OIDC client to use
(`GET /api/auth-config`), logs you in with it, and saves the tokens for that server in
`~/.arm/credentials.json` (0600). It also saves that server as your default `url`, so
`arm login --url https://arm-beta.ovea.pro` switches you to beta. Later commands send the saved
token (unless `--token`/`ARM_TOKEN` is given) and refresh it when it has expired;
`arm logout` forgets it.

- **Browser** (default): authorization code with PKCE, redirected to a listener on
  `http://127.0.0.1:<random port>/callback`. The URL is printed too, in case no browser opens.
- **Device code** (`--device`; the default over SSH or without a display): open the printed URL
  on any device, approve, and the CLI picks up the token.

The server's OIDC client must allow both: a loopback redirect
(`^http://(127\.0\.0\.1|localhost):[0-9]+/callback$`, regex) and the device code grant.

## Defaults

The API requires `provider`, `model` and `caller` on every create (and `caller` on every kill);
the CLI fills them in so you don't have to type them. Each comes from, in order: the flag, the
environment, your settings in `~/.arm/config.json`, then a built-in default:

| Setting | Env | Built-in default |
|---|---|---|
| `url` | `ARM_URL` | `http://localhost:5000` |
| `provider` | `ARM_PROVIDER` | `claude` |
| `model` | `ARM_MODEL` | `sonnet` |
| `caller` | `ARM_CALLER` | your OS user name (`$USER`) |

`arm config set url https://arm-beta.ovea.pro` saves one; `arm config` lists them and where the
file is. `ARM_HOME` moves the folder (default `~/.arm`).

`arm session create` prints `Started session <id>.` when the session got a slot (201) and
`Queued session <id> at position N.` when it is waiting for one (202); with `--json` it prints the
session either way (`status` and `queuePosition` tell them apart). `arm session list` pages newest
first; its footer says which matches are shown and the `--offset` of the next page.

`arm events` tails `GET /api/events` (Hey API's SSE client) until Ctrl+C, which exits `0`. By
default it prints one line per event (`12:03:04 session.created <sessionId> "prompt"`, local
time; heartbeats hidden; `session.killed` shows `source=`, `caller=` when a user killed it, and
the reason); with `--json` it prints NDJSON, one `{"event","id","data"}` object per
line (heartbeats included, without an `id`), e.g. `arm events --json | jq .data`. Filters are
enforced by the server (`session.*` matches a namespace; an unknown name is a 400, exit 1). A
dropped connection is retried with backoff; a stream the server ends (e.g. a redeploy) is
reopened with `Last-Event-ID`, so nothing retained is missed. `--last-event-id` starts from an id
seen in earlier `--json` output.

### Exit codes

| Code | Meaning |
|---|---|
| `0` | Success (and Ctrl+C on `arm events`) |
| `1` | Any other API error (400, 401, 5xx other than 503, …), a network error, or an unexpected failure |
| `2` | Usage error: unknown command or option, missing argument, malformed option value |
| `3` | Not found (404), e.g. no such session |
| `4` | Conflict (409): killing a session that already ended, deleting one that hasn't |
| `5` | Unavailable (503): the session queue is full, or the server is draining |

Errors always go to **stderr**, so stdout only ever holds successful output. The API's error
envelope `{ "error": { code, message, details } }` is printed as `error: 404 Not Found:
SESSION_NOT_FOUND: …`, followed by one line per entry of `details.problems` when there are
several. With `--json`, the error is printed to stderr as JSON: the API's envelope when there is
one, otherwise an envelope of the same shape (`HTTP_<status>`, `NETWORK_ERROR`, `USAGE`).

## Develop

```bash
npm run typecheck   # tsc --noEmit (regenerates the client first)
npm test            # bun test: parsing, formatting, exit codes, commands against a fake fetch
npm start -- session list   # run from source with bun
npm run build       # → artifacts/cli/arm (single binary, ~80 MB, depends only on libc)
npm run build:all   # → artifacts/cli/arm-{linux-x64,linux-arm64,darwin-arm64} (downloads Bun runtimes)
```

`prebuild`/`pretest`/`pretypecheck` run the root `npm run generate`. `@arm/client` is a tsconfig
path alias to `artifacts/clients/ts` (Bun honours it when bundling).

## Adding commands

Each command group is a `CommandGroup` in `src/commands/<group>.ts` (name, summary, and commands
with their positional `args`, `options` and an async `run` that returns `{ json, pretty }`, or
`undefined` after streaming its own lines through `ctx.stdout`, as `events` does until
`ctx.signal` aborts). A group's `defaultCommand` runs when none is named (`arm events`).
Register it in `src/commands/index.ts`; parsing, help, `--json`/`--pretty` output and exit codes
come from the runner in `src/cli.ts`.
