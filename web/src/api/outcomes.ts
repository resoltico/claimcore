import { mutationUncertain } from "../protocol/delivery";
import type { ApiResult, EndpointOutcome } from "../protocol/types";
import { localNotice, type Notice } from "../protocol/notices";
const humanNotice = (value: EndpointOutcome): Notice | null => {
  if ("representation" in value && value.representation === "HUMAN") {
    if (value.outcome.kind === "REJECTED") {
      return { kind: "human", advice: value.outcome.rejection.advice };
    }
    if (value.outcome.kind === "FAILED") {
      return { kind: "human", advice: value.outcome.advice };
    }
  }
  return null;
};
export const resultNotice = (result: ApiResult<EndpointOutcome>): Notice => {
  if (result.kind === "deliveryFailure") {
    return result.notice;
  }
  if (result.kind === "hostFailure") {
    return result.failure.code === "CONTRACT_MISMATCH"
      ? localNotice("definitionMismatch")
      : {
          kind: "diagnostic",
          diagnostic: result.failure,
        };
  }
  const { value } = result;
  const human = humanNotice(value);
  if (human !== null) {
    return human;
  }
  const { outcome } = value;
  if ((outcome.kind === "REJECTED" || outcome.kind === "FAILED") && "data" in outcome) {
    return {
      kind: "diagnostic",
      diagnostic: outcome.data,
    };
  }
  if (outcome.kind === "PENDING") {
    return { kind: "pending", finalAt: outcome.data.finalAt };
  }
  if (outcome.kind === "NOT_ACCEPTED") {
    return localNotice("notAccepted");
  }
  if (outcome.kind === "OUTCOME_UNKNOWN") {
    return localNotice("outcomeUnknown");
  }
  return localNotice("incomplete");
};
export const isMutationUncertain = mutationUncertain;
export const isDisclosureRefused = (result: ApiResult<EndpointOutcome>): boolean => {
  if (result.kind === "hostFailure") {
    return result.failure.code === "SESSION_REJECTED";
  }
  if (result.kind !== "outcome") {
    return false;
  }
  const { value } = result;
  if ("representation" in value && value.representation === "HUMAN") {
    return (
      value.outcome.kind === "REJECTED" &&
      value.outcome.rejection.advice.action === "ASK_FOR_ACCESS"
    );
  }
  return (
    value.outcome.kind === "REJECTED" &&
    "data" in value.outcome &&
    value.outcome.data.code === "RESOURCE_UNAVAILABLE"
  );
};
