import type { CommandGroup } from "../registry";
import { configGroup } from "./config";
import { eventsGroup } from "./events";
import { sessionGroup } from "./session";

/** Every command group the CLI exposes. Later milestones add their groups here. */
export const groups: readonly CommandGroup[] = [sessionGroup, eventsGroup, configGroup];
