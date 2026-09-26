import type { CommandGroup } from "../registry";
import { eventsGroup } from "./events";
import { messageGroup } from "./message";

/** Every command group the CLI exposes. Later milestones add their groups here. */
export const groups: readonly CommandGroup[] = [messageGroup, eventsGroup];
