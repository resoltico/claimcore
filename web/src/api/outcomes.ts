import type { ApiResult, EndpointOutcome } from "./types";
import { localNotice, type Notice } from "./notices";
export const resultNotice = (result: ApiResult<EndpointOutcome>): Notice => {
  if (result.kind === "deliveryFailure") {
    return result.notice;
  }
  if (result.kind === "hostFailure") {
    return result.failure.code === "CONTRACT_MISMATCH"
      ? localNotice("definitionMismatch")
      : { kind: "diagnostic", diagnostic: result.failure.diagnostic };
  }
  const { outcome } = result.value;
  if (outcome.tag === "REJECTED" || outcome.tag === "FAILED") {
    return { kind: "diagnostic", diagnostic: outcome.data.diagnostic };
  }
  if (outcome.tag === "CANCELLED") {
    return localNotice("cancelled");
  }
  if (outcome.tag === "OUTCOME_UNKNOWN") {
    return localNotice("outcomeUnknown");
  }
  return localNotice("incomplete");
};
export const isMutationUncertain = (result: ApiResult<EndpointOutcome>): boolean =>
  result.kind === "deliveryFailure" ||
  (result.kind === "hostFailure" && result.failure.executionPhase !== "NOT_STARTED") ||
  (result.kind === "outcome" && result.value.outcome.tag === "OUTCOME_UNKNOWN");
export const isDisclosureRefused = (result: ApiResult<EndpointOutcome>): boolean =>
  (result.kind === "hostFailure" &&
    (result.failure.code === "SESSION_REJECTED" || result.failure.code === "SESSION_FORBIDDEN")) ||
  (result.kind === "outcome" &&
    result.value.outcome.tag === "REJECTED" &&
    result.value.outcome.data.diagnostic.id === "ACCESS_RESOURCE_UNAVAILABLE");
