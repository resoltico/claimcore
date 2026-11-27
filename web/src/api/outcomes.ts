import type { AttachmentTransferFailureReply } from "@contracts/client";
import type { ApiResult, EndpointOutcome } from "../protocol/types";
import { localNotice, type Notice } from "../protocol/notices";
const humanNotice = (value: EndpointOutcome): Notice | null => {
  if ("representation" in value && value.representation === "HUMAN") {
    if (value.outcome.kind === "REJECTED") {
      return { kind: "human", advice: value.outcome.rejection.advice };
    }
  }
  if (value.outcome.kind === "FAILED") {
    return {
      kind: "human",
      advice: {
        message: { messageId: value.outcome.fault.messageId, arguments: [] },
        action: value.outcome.fault.action,
      },
    };
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
  if (outcome.kind === "PENDING" && "document" in outcome) {
    return {
      kind: "pending",
      screen: outcome.document.screen,
    };
  }
  if (outcome.kind === "NOT_ACCEPTED") {
    return localNotice("notAccepted");
  }
  if (outcome.kind === "RECEIPT_UNAVAILABLE") {
    return localNotice("receiptUnavailable");
  }
  if (outcome.kind === "OUTCOME_UNKNOWN") {
    return localNotice("outcomeUnknown");
  }
  return localNotice("incomplete");
};
export const isDisclosureRefused = (
  result: ApiResult<EndpointOutcome | AttachmentTransferFailureReply>,
): boolean => {
  if (result.kind === "hostFailure") {
    return (
      result.failure.code === "SESSION_REJECTED" || result.failure.code === "ANTIFORGERY_REJECTED"
    );
  }
  if (result.kind !== "outcome") {
    return false;
  }
  const { outcome } = result.value;
  if (outcome.kind !== "REJECTED" || !("rejection" in outcome)) {
    return false;
  }
  const { rejection } = outcome;
  return "advice" in rejection
    ? rejection.advice.action === "ASK_FOR_ACCESS"
    : rejection.code === "RESOURCE_UNAVAILABLE";
};
