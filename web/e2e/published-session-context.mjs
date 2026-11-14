/** Check only the owner facts consumed by the rollover witness. */
/** @param {unknown} reply */
export const isActiveOwnerContext = (reply) =>
  reply?.representation === "DATA" &&
  reply?.outcome?.kind === "SUCCEEDED" &&
  reply.outcome.data?.requester?.kind === "ACTIVE" &&
  Array.isArray(reply.outcome.data.requester.capabilities) &&
  reply.outcome.data.requester.capabilities.includes("MANAGE_ACCESS");
