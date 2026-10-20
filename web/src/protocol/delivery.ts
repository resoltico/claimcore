import { deliveryPolicy, type EndpointId } from "@contracts/client";
import type { ApiResult, EndpointOutcome } from "../api/types";
export type AttemptKnowledge = (typeof deliveryPolicy.classifications)[number]["knowledge"];
export type AccumulatedKnowledge = (typeof deliveryPolicy.accumulation)[number]["result"];
export type ResolutionEvidence = (typeof deliveryPolicy.classifications)[number]["evidence"];
const attemptKey = (result: ApiResult<EndpointOutcome>, dispatched: boolean): string => {
  if (!dispatched || (result.kind === "deliveryFailure" && result.dispatched === false)) {
    return "NOT_DISPATCHED";
  }
  if (result.kind === "deliveryFailure") {
    return "DISPATCHED_WITHOUT_REPLY";
  }
  if (result.kind === "hostFailure") {
    return `HOST/${result.failure.executionPhase ?? "NONE"}`;
  }
  const conflict =
    result.value.outcome.tag === "REJECTED" &&
    result.value.outcome.data.diagnostic.id === "OPERATION_CONTENT_CONFLICT";
  return `OUTCOME/${result.value.outcome.tag}${conflict ? "/CONFLICT" : ""}`;
};
const classification = (
  endpoint: EndpointId,
  result: ApiResult<EndpointOutcome>,
  dispatched: boolean,
) => {
  const key = `${endpoint}/${attemptKey(result, dispatched)}`;
  const row = deliveryPolicy.classifications.find((item) => item.key === key);
  if (row === undefined) {
    throw new Error("Missing core delivery classification.");
  }
  return row;
};
export const attemptKnowledge = (
  endpoint: EndpointId,
  result: ApiResult<EndpointOutcome>,
  dispatched = true,
): AttemptKnowledge => classification(endpoint, result, dispatched).knowledge;
export const accumulateKnowledge = (
  prior: AccumulatedKnowledge,
  attempt: AttemptKnowledge,
  evidence: ResolutionEvidence,
): AccumulatedKnowledge => {
  const key = `${prior}/${attempt}/${evidence}`;
  const row = deliveryPolicy.accumulation.find((item) => item.key === key);
  if (row === undefined) {
    throw new Error("Missing core delivery transition.");
  }
  return row.result;
};
export const resolutionEvidence = (result: ApiResult<EndpointOutcome>): ResolutionEvidence =>
  result.kind === "outcome" ? classification(result.value.endpoint, result, true).evidence : "NONE";
