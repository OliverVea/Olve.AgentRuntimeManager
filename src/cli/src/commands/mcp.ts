import { type McpServer, mcpServersDelete, mcpServersGet, mcpServersList, mcpServersSet } from "@arm/client";
import { unwrap } from "../api";
import { keyValues } from "../options";
import { formatKeyValue, formatTable } from "../output";
import type { CommandGroup } from "../registry";

const nameArg = { name: "name", description: "The server's name in ARM (what sessions name in --tool): lower-case letters, digits and dashes" };

/** How a server starts, on one line (`--json` has the arguments apart). */
function commandLine(s: McpServer): string {
  return [s.command, ...s.args].join(" ");
}

/** One row per server: its name, how it starts and what it's for. */
export function formatMcpServers(servers: readonly McpServer[]): string {
  if (servers.length === 0) return "No MCP servers registered.";
  return formatTable(servers, [
    { header: "server", value: (s: McpServer) => s.name },
    { header: "command", value: commandLine },
    { header: "description", value: (s: McpServer) => s.description },
  ]);
}

/** Key/value lines for one server; no env is left out. */
export function formatMcpServer(s: McpServer): string {
  const env = Object.entries(s.env).map(([k, v]) => `${k}=${v}`);
  const entries: Array<readonly [string, unknown]> = [
    ["name", s.name],
    ["description", s.description],
    ["command", commandLine(s)],
    ["env", env.length ? env.join(" ") : undefined],
    ["updatedAt", s.updatedAt],
  ];
  return formatKeyValue(entries.filter(([, v]) => v !== undefined));
}

export const mcpGroup: CommandGroup = {
  name: "mcp",
  summary: "External MCP servers registered for agents, which sessions name in --tool to get their tools",
  defaultCommand: "list",
  commands: [
    {
      name: "list",
      summary: "Every registered MCP server",
      args: [],
      options: {},
      async run({ client }) {
        const servers = await unwrap(mcpServersList({ client }), client);
        return { json: servers, pretty: formatMcpServers(servers) };
      },
    },
    {
      name: "get",
      summary: "Show one MCP server: how it starts and the variables it gets",
      args: [nameArg],
      options: {},
      async run({ args, client }) {
        const server = await unwrap(mcpServersGet({ client, path: { name: args.name! } }), client);
        return { json: server, pretty: formatMcpServer(server) };
      },
    },
    {
      name: "set",
      summary:
        "Register an MCP server, or replace it whole; `--` keeps the command's own flags apart: arm mcp set memory 'A knowledge graph' --env A=1 -- npx -y pkg@1.2.3",
      args: [
        nameArg,
        { name: "description", description: "What it's for, in one line (quote it)" },
        { name: "command", description: "The program to start, looked up on the agent's PATH, e.g. npx" },
      ],
      rest: { name: "args", description: "Its arguments" },
      options: {
        env: {
          type: "string",
          description: "A variable it gets on top of the agent's environment (not for secrets: anyone with API access reads it)",
          valueName: "NAME=VALUE",
          multiple: true,
        },
      },
      async run({ args, rest, options, client }) {
        const server = await unwrap(
          mcpServersSet({
            client,
            path: { name: args.name! },
            body: { description: args.description!, command: args.command!, args: rest, env: keyValues(options, "env") ?? {} },
          }),
          client,
        );
        return { json: server, pretty: `Set ${server.name}. It starts when a session names it: --tool ${server.name}.` };
      },
    },
    {
      name: "delete",
      summary: "Unregister an MCP server (sessions that took it keep their copy)",
      args: [nameArg],
      options: {},
      async run({ args, client }) {
        await unwrap(mcpServersDelete({ client, path: { name: args.name! } }), client);
        return { json: {}, pretty: `Deleted ${args.name} (sessions that took it keep their copy).` };
      },
    },
  ],
};
