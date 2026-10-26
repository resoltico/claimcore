import type { ApiResult, EndpointOutcome } from "./types";

/** Only a correlated acceptance can resolve uncertainty about an earlier attempt. */
export const mutationAccepted = (result: ApiResult<EndpointOutcome>): boolean =>
  result.kind === "outcome" &&
  (result.value.outcome.kind === "ACCEPTED" || result.value.outcome.kind === "ALREADY_ACCEPTED");

export const mutationUncertain = (result: ApiResult<EndpointOutcome>): boolean => {
  if (result.kind === "deliveryFailure") {
    return result.dispatched !== false;
  }
  if (result.kind === "hostFailure") {
    return result.failure.phase !== "NOT_STARTED";
  }
  return result.value.outcome.kind === "OUTCOME_UNKNOWN";
};
