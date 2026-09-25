# arm — CLI

Command-line client for Olve.AgentRuntimeManager. It is hand-written on the Hey API TypeScript
client that `npm run generate` (repo root) produces in `artifacts/clients/ts`, and it is compiled to a
single self-contained binary with `bun build --compile`.

## Usage

```
arm <group> <command> [arguments] [options]

arm message list [--page N] [--page-size N]
arm message get <id>
arm message create <text>
arm message update <id> <text>
arm message delete <id>

arm --help | arm <group> --help | arm <group> <command> --help
arm --version
```

Global options (any position):

| Option | Env | Default | |
|---|---|---|---|
| `--url URL` | `ARM_URL` | `http://localhost:5000` | API base URL (the backend's local dev port) |
| `--token TOKEN` | `ARM_TOKEN` | none | Sent as `Authorization: Bearer …`. Writes (create/update/delete) need it |
| `--json` | | | Print the raw API JSON |
| `--pretty` | | on | Human-readable: a table for lists, key/value for objects |

Precedence is flag, then env, then default. Use `--` for text that starts with a dash:
`arm message create -- "-5 degrees"`.

Exit codes: `0` success, `1` API or network error, `2` usage error. Errors always go to **stderr**,
so stdout only ever holds successful output. With `--json`, the error is printed to stderr as JSON:
the API's error body when there is one, otherwise a `{ "error": { code, message, details } }`
envelope (`HTTP_<status>`, `NETWORK_ERROR`, `USAGE`).

## Develop

```bash
npm run typecheck   # tsc --noEmit (regenerates the client first)
npm test            # bun test: parsing, formatting, exit codes, commands against a fake fetch
npm start -- message list   # run from source with bun
npm run build       # → artifacts/cli/arm (single binary, ~80 MB, depends only on libc)
npm run build:all   # → artifacts/cli/arm-{linux-x64,linux-arm64,darwin-arm64} (downloads Bun runtimes)
```

`prebuild`/`pretest`/`pretypecheck` run the root `npm run generate`. `@arm/client` is a tsconfig
path alias to `artifacts/clients/ts` (Bun honours it when bundling).

## Adding commands

Each command group is a `CommandGroup` in `src/commands/<group>.ts` (name, summary, and commands
with their positional `args`, `options` and an async `run` that returns `{ json, pretty }`).
Register it in `src/commands/index.ts`; parsing, help, `--json`/`--pretty` output and exit codes
come from the runner in `src/cli.ts`.
