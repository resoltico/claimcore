import { attemptKnowledge } from "../protocol/delivery";
import type { ApiResult, EndpointOutcome } from "./types";
import { localNotice, type Notice } from "./notices";
export const resultNotice = (result: ApiResult<EndpointOutcome>): Notice => {
  if (result.kind === "deliveryFailure") {
    return result.notice;
  }
  if (result.kind === "hostFailure") {
    return result.failure.code === "CONTRACT_MISMATCH"
      ? localNotice("definitionMismatch")
      : {
          kind: "diagnostic",
          diagnostic: {
            code: result.failure.diagnostic.id,
            limits: result.failure.diagnostic.parameters,
          },
        };
  }
  const { outcome } = result.value;
  if (outcome.tag === "REJECTED" || outcome.tag === "FAILED") {
    return {
      kind: "diagnostic",
      diagnostic: {
        code: outcome.data.code,
        limits: "limits" in outcome.data ? outcome.data.limits : {},
      },
    };
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
  attemptKnowledge(result.kind === "outcome" ? result.value.endpoint : "case.execute", result) ===
  "UNCONFIRMED";
export const isDisclosureRefused = (result: ApiResult<EndpointOutcome>): boolean =>
  (result.kind === "hostFailure" &&
    (result.failure.code === "SESSION_REJECTED" || result.failure.code === "SESSION_FORBIDDEN")) ||
  (result.kind === "outcome" &&
    result.value.outcome.tag === "REJECTED" &&
    result.value.outcome.data.code === "ACCESS_RESOURCE_UNAVAILABLE");
