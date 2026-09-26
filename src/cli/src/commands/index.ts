import type { CommandGroup } from "../registry";
import { configGroup } from "./config";
import { eventsGroup } from "./events";
import { loginGroup, logoutGroup } from "./login";
import { sessionGroup } from "./session";

/** Every command group the CLI exposes. Later milestones add their groups here. */
export const groups: readonly CommandGroup[] = [loginGroup, logoutGroup, sessionGroup, eventsGroup, configGroup];
