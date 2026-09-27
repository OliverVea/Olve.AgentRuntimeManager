import { type EnvVariable, envDelete, envList, envSet } from "@arm/client";
import { unwrap } from "../api";
import { localDateTime, formatTable } from "../output";
import type { CommandGroup } from "../registry";

const nameArg = { name: "name", description: "The variable's name, e.g. GIT_AUTHOR_NAME" };

/** One row per registered variable. */
export function formatEnv(variables: readonly EnvVariable[]): string {
  if (variables.length === 0) return "No environment variables registered.";
  return formatTable(variables, [
    { header: "name", value: (v: EnvVariable) => v.name },
    { header: "value", value: (v: EnvVariable) => v.value },
    { header: "default", value: (v: EnvVariable) => (v.default ? "yes" : "no") },
    { header: "updated", value: (v: EnvVariable) => localDateTime(v.updatedAt) },
  ]);
}

export const envGroup: CommandGroup = {
  name: "env",
  summary: "Environment variables registered for agents (not secrets: anyone with API access reads them)",
  defaultCommand: "list",
  commands: [
    {
      name: "list",
      summary: "Every registered environment variable",
      args: [],
      options: {},
      async run({ client }) {
        const variables = await unwrap(envList({ client }), client);
        return { json: variables, pretty: formatEnv(variables) };
      },
    },
    {
      name: "set",
      summary: "Register an environment variable, or replace it",
      args: [nameArg, { name: "value", description: "Its value (quote it)" }],
      options: {
        default: { type: "boolean", description: "Give it to every agent (else only to sessions that --use-env it)" },
      },
      async run({ args, options, client }) {
        const variable = await unwrap(
          envSet({ client, path: { name: args.name! }, body: { value: args.value!, default: options.default === true } }),
          client,
        );
        return { json: variable, pretty: `Set ${variable.name}${variable.default ? " (every agent gets it)" : ""}.` };
      },
    },
    {
      name: "delete",
      summary: "Unregister an environment variable (sessions that took it keep their value)",
      args: [nameArg],
      options: {},
      async run({ args, client }) {
        await unwrap(envDelete({ client, path: { name: args.name! } }), client);
        return { json: {}, pretty: `Deleted ${args.name}.` };
      },
    },
  ],
};
