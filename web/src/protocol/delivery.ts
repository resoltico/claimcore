import type { ApiResult, EndpointOutcome } from "./types";

export const mutationUncertain = (result: ApiResult<EndpointOutcome>): boolean => {
  if (result.kind === "deliveryFailure") {
    return result.dispatched !== false;
  }
  if (result.kind === "hostFailure") {
    return result.failure.phase !== "NOT_STARTED";
  }
  return result.value.outcome.kind === "OUTCOME_UNKNOWN";
};
