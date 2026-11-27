import type { ApiResult, EndpointOutcome } from "./types";

export const mutationUncertain = (result: ApiResult<EndpointOutcome>): boolean => {
  if (result.kind === "deliveryFailure") {
    return result.dispatched !== false;
  }
  if (result.kind === "hostFailure") {
    return result.failure.phase !== "NOT_STARTED";
  }
  return ["OUTCOME_UNKNOWN", "RECEIPT_UNAVAILABLE"].includes(result.value.outcome.kind);
};
