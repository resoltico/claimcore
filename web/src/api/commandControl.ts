import { apiEndpoints } from "./service";
import type { Affordance } from "@contracts/client";

/** Core supplies the complete validated operational request. */
export const assembleCommand = (action: Affordance) => {
  if (action.binding.kind !== "COMMAND") {return null;}
  const { command, ...input } = action.binding.request;
  switch (command) {
    case apiEndpoints.peopleRequestsDismiss:
      return { endpoint: command, input };
  }
};
