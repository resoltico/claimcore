import {
  endpoints,
  deliveryOutcomePolicies,
  deliveryMissingPolicies,
  deliveryHostPolicies,
  deliveryResolutionPolicies,
  unresolvedKnowledge,
  resolutionEvidencePolicies,
  type EndpointId,
} from "@contracts/endpoint-catalog";
import type { ApiResult, EndpointOutcome } from "../api/types";
export const attemptKnowledge = (
  endpoint: EndpointId,
  result: ApiResult<EndpointOutcome>,
  dispatched = true,
): string => {
  if (result.kind === "deliveryFailure" && result.dispatched === false) {
    dispatched = false;
  }
  const { mutates } = endpoints.find((item) => item.id === endpoint)!;
  if (!dispatched || result.kind === "deliveryFailure") {
    return deliveryMissingPolicies.find((row) => row.Item1 === dispatched && row.Item2 === mutates)!
      .Item3;
  }
  if (result.kind === "hostFailure") {
    return deliveryHostPolicies.find(
      (row) =>
        row.Item1 === mutates && row.Item2 === (result.failure.executionPhase === "NOT_STARTED"),
    )!.Item3;
  }
  const row = deliveryOutcomePolicies.find(
    (item) => item.Item1 === result.value.outcome.tag && item.Item2 === mutates,
  );
  if (row === undefined) {
    throw new Error("Uncovered endpoint outcome family.");
  }
  return row.Item3;
};
export const accumulateKnowledge = (prior: string, attempt: string, evidence: string): string =>
  deliveryResolutionPolicies.find((row) => row.Item1 === evidence)?.Item2 ??
  (prior === unresolvedKnowledge ? prior : attempt);
export const resolutionEvidence = (result: ApiResult<EndpointOutcome>): string => {
  if (result.kind !== "outcome" || !("operationId" in result.value)) {
    return "NONE";
  }
  const endpoint = endpoints.find((item) => item.id === result.value.endpoint)!;
  if (endpoint.receiptScope === "NONE") {
    return "NONE";
  }
  const { outcome } = result.value;
  const knowledge = attemptKnowledge(result.value.endpoint, result);
  const diagnostic = outcome.tag === "REJECTED" ? outcome.data.diagnostic.id : "";
  return (
    resolutionEvidencePolicies.find(
      (row) =>
        (row.Item1 === "*" || row.Item1 === knowledge) &&
        (row.Item2 === "*" || row.Item2 === outcome.tag) &&
        (row.Item3 === "*" || row.Item3 === diagnostic),
    )?.Item4 ?? "NONE"
  );
};
