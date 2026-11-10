import { isResponseFor } from "../../contracts/client.ts";

/** Only the current admitted ACTIVE requester can prove the rollover fixture's owner access. */
/** @param {unknown} reply */
export const isActiveOwnerContext = (reply) =>
  isResponseFor("context", "DATA", reply) &&
  reply.outcome.kind === "SUCCEEDED" &&
  reply.outcome.data.requester.kind === "ACTIVE" &&
  reply.outcome.data.requester.capabilities.includes("MANAGE_AUTHORITY");
