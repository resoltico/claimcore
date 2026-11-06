import type { ActorRef } from "@contracts/client";
import type { Presentation } from "./context";
/** Display names are authored by the core; erased names use its localized label. */
export const actorName = (p: Pick<Presentation, "text">, actor: ActorRef): string =>
  actor.displayName.kind === "NAMED"
    ? actor.displayName.value
    : p.text("label.formerAccount", { id: actor.actorId.slice(0, 8) });
