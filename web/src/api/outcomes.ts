import { mutationUncertain } from "../protocol/delivery";
import type { ApiResult, EndpointOutcome } from "../protocol/types";
import { localNotice, type Notice } from "../protocol/notices";
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
            code: result.failure.code,
            limits: {},
          },
        };
  }
  const { outcome } = result.value;
  if (outcome.kind === "REJECTED" || outcome.kind === "FAILED") {
    return {
      kind: "diagnostic",
      diagnostic: {
        code: outcome.data.code,
        limits: "limits" in outcome.data ? outcome.data.limits : {},
      },
    };
  }
  if (outcome.kind === "OUTCOME_UNKNOWN") {
    return localNotice("outcomeUnknown");
  }
  return localNotice("incomplete");
};
export const isMutationUncertain = mutationUncertain;
export const isDisclosureRefused = (result: ApiResult<EndpointOutcome>): boolean =>
  (result.kind === "hostFailure" &&
    (result.failure.code === "SESSION_REJECTED" || result.failure.code === "SESSION_FORBIDDEN")) ||
  (result.kind === "outcome" &&
    result.value.outcome.kind === "REJECTED" &&
    result.value.outcome.data.code === "ACCESS_RESOURCE_UNAVAILABLE");
