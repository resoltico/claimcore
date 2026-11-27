import { apiEndpoints } from "./service";
import type { Affordance } from "@contracts/client";

/** The inbox posts the verified principal supplied by its dismissal control. */
export const assembleDismissal = (action: Affordance) => {
  if (
    action.binding.kind !== "COMMAND" ||
    action.binding.request.command !== apiEndpoints.peopleRequestsDismiss
  ) {
    return null;
  }
  const { command, ...input } = action.binding.request;
  return { endpoint: command, input };
};
