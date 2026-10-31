import { isObject } from "./client";
import type { EndpointId } from "@contracts/client";
const object = (value: unknown): Record<string, unknown> => (isObject(value) ? value : {});
const identity = (value: unknown): string | null =>
  typeof value === "string" ? value.toLowerCase() : null;
const reference = (value: unknown): string | null =>
  typeof value === "string" ? value.toUpperCase() : null;
const operationEnvelopeMatches = (
  endpoint: EndpointId,
  request: Record<string, unknown>,
  envelope: Record<string, unknown>,
): boolean => {
  if (
    !["operation.preview", "operation.execute", "operation.observe", "operation.resolve"].includes(
      endpoint,
    )
  ) {
    return true;
  }
  const expected = identity(request["operationId"]);
  return expected !== null && expected === identity(envelope["operationId"]);
};
const receiptMatches = (
  endpoint: EndpointId,
  request: Record<string, unknown>,
  outcome: Record<string, unknown>,
): boolean => {
  const receipt =
    outcome["kind"] === "ACCEPTED" ||
    outcome["kind"] === "ALREADY_ACCEPTED" ||
    (endpoint === "operation.observe" && outcome["kind"] === "SUCCEEDED");
  if (!receipt) {
    return true;
  }
  const data = object(outcome["data"]);
  if (identity(data["operationId"]) !== identity(request["operationId"])) {
    return false;
  }
  const subject = object(data["subject"]);
  return (
    request["kind"] !== "CASE" ||
    (subject["kind"] === "CASE" &&
      reference(request["caseReference"]) === reference(subject["caseReference"]))
  );
};
const readTargetMatches = (
  endpoint: EndpointId,
  request: Record<string, unknown>,
  outcome: Record<string, unknown>,
): boolean => {
  if (outcome["kind"] !== "SUCCEEDED") {
    return true;
  }
  const data = object(outcome["data"]);
  if (endpoint === "case.get") {
    return reference(request["caseReference"]) === reference(data["caseReference"]);
  }
  if (endpoint === "authority.grants") {
    return identity(request["actorId"]) === identity(object(data["actor"])["actorId"]);
  }
  return true;
};
/** Structural decoding is a prerequisite; these are transport identities, not business rules. */
export const replyCorrelates = (endpoint: EndpointId, input: unknown, reply: unknown): boolean => {
  const request = object(input);
  const envelope = object(reply);
  const outcome = object(envelope["outcome"]);
  return (
    operationEnvelopeMatches(endpoint, request, envelope) &&
    receiptMatches(endpoint, request, outcome) &&
    readTargetMatches(endpoint, request, outcome)
  );
};
