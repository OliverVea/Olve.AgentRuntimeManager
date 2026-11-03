import { type Tool, toolsList } from "@arm/client";
import { unwrap } from "../api";
import { formatTable } from "../output";
import type { CommandGroup } from "../registry";

/** One row per tool: its name (what a session names in `--tool`), whether a session without `--tool` gets it, and what it does. */
export function formatTools(tools: readonly Tool[]): string {
  if (tools.length === 0) return "No tools.";
  return formatTable(tools, [
    { header: "tool", value: (t: Tool) => t.name },
    { header: "default", value: (t: Tool) => (t.default ? "yes" : "no") },
    { header: "description", value: (t: Tool) => t.description },
  ]);
}

export const toolGroup: CommandGroup = {
  name: "tool",
  summary: "What sessions name to give their agent tools: ARM's own and registered MCP servers, instead of the provider's",
  defaultCommand: "list",
  commands: [
    {
      name: "list",
      summary: "Every tool sessions may name",
      args: [],
      options: {},
      async run({ client }) {
        const tools = await unwrap(toolsList({ client }), client);
        return { json: tools, pretty: formatTools(tools) };
      },
    },
  ],
};
