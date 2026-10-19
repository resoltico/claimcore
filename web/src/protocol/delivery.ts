import { endpoints, type EndpointId } from "@contracts/client";
import type { ApiResult, EndpointOutcome } from "../api/types";
export type AttemptKnowledge =
  "NOT_SENT" | "READ_SUCCEEDED" | "READ_FAILED" | "ACCEPTED" | "NOT_ACCEPTED" | "UNCONFIRMED";
export type AccumulatedKnowledge = AttemptKnowledge | "CONTENT_EXCLUDED";
export type ResolutionEvidence = "NONE" | "CONTENT_ACCEPTED" | "CONTENT_EXCLUDED";
const outcomeKnowledge = (
  tag: EndpointOutcome["outcome"]["tag"],
  mutates: boolean,
): AttemptKnowledge => {
  switch (tag) {
    case "ACCEPTED":
    case "ALREADY_ACCEPTED":
      return "ACCEPTED";
    case "SUCCEEDED":
    case "REVIEWED":
      return "READ_SUCCEEDED";
    case "REJECTED":
    case "FAILED":
    case "CANCELLED":
      return mutates ? "NOT_ACCEPTED" : "READ_FAILED";
    case "OUTCOME_UNKNOWN":
      return "UNCONFIRMED";
    default:
      throw new Error("Uncovered endpoint outcome family.");
  }
};
export const attemptKnowledge = (
  endpoint: EndpointId,
  result: ApiResult<EndpointOutcome>,
  dispatched = true,
): AttemptKnowledge => {
  const { mutates } = endpoints.find((item) => item.id === endpoint)!;
  if (!dispatched || (result.kind === "deliveryFailure" && result.dispatched === false)) {
    return "NOT_SENT";
  }
  if (result.kind === "deliveryFailure") {
    return mutates ? "UNCONFIRMED" : "READ_FAILED";
  }
  if (result.kind === "hostFailure") {
    if (!mutates) {
      return "READ_FAILED";
    }
    return result.failure.executionPhase === "NOT_STARTED" ? "NOT_ACCEPTED" : "UNCONFIRMED";
  }
  return outcomeKnowledge(result.value.outcome.tag, mutates);
};
export const accumulateKnowledge = (
  prior: AccumulatedKnowledge,
  attempt: AttemptKnowledge,
  evidence: ResolutionEvidence,
): AccumulatedKnowledge => {
  if (evidence === "CONTENT_ACCEPTED") {
    return "ACCEPTED";
  }
  if (evidence === "CONTENT_EXCLUDED") {
    return "CONTENT_EXCLUDED";
  }
  return prior === "UNCONFIRMED" ? prior : attempt;
};
export const resolutionEvidence = (result: ApiResult<EndpointOutcome>): ResolutionEvidence => {
  if (result.kind !== "outcome" || !("operationId" in result.value)) {
    return "NONE";
  }
  const endpoint = endpoints.find((item) => item.id === result.value.endpoint)!;
  if (endpoint.receiptScope === null) {
    return "NONE";
  }
  if (attemptKnowledge(result.value.endpoint, result) === "ACCEPTED") {
    return "CONTENT_ACCEPTED";
  }
  return result.value.outcome.tag === "REJECTED" &&
    result.value.outcome.data.diagnostic.id === "OPERATION_CONTENT_CONFLICT"
    ? "CONTENT_EXCLUDED"
    : "NONE";
};
