import { serverInfoApiGet } from "@arm/client";
import { unwrap } from "../api";
import { formatKeyValue } from "../output";
import type { CommandGroup } from "../registry";

export const serverGroup: CommandGroup = {
  name: "server",
  summary: "The server itself",
  defaultCommand: "info",
  commands: [
    {
      name: "info",
      summary: "Which build the server runs and where (both empty on a local run)",
      args: [],
      options: {},
      async run({ client }) {
        const info = await unwrap(serverInfoApiGet({ client }), client);
        return {
          json: info,
          pretty: formatKeyValue([
            ["version", info.version],
            ["environment", info.environment],
          ]),
        };
      },
    },
  ],
};
