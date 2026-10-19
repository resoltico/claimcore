import { endpoints, type EndpointId } from "@contracts/client";
const object = (value: unknown): value is Record<string, unknown> =>
  typeof value === "object" && value !== null && !Array.isArray(value);
const member = (value: unknown, name: string): unknown => (object(value) ? value[name] : undefined);
const fold = (value: unknown): unknown =>
  typeof value === "string"
    ? value.replace(/[A-Z]/gu, (character) => String.fromCharCode(character.charCodeAt(0) + 32))
    : undefined;
const successor = (expected: unknown, actual: unknown): boolean =>
  typeof expected === "number" && Number.isSafeInteger(expected + 1) && actual === expected + 1;
const receipt = (endpoint: EndpointId, request: unknown, value: unknown): boolean => {
  if (
    member(value, "operationId") !== member(request, "operationId") ||
    member(value, "act") !== member(member(request, "act"), "kind")
  ) {
    return false;
  }
  if (endpoint === "authority.execute") {
    return member(value, "scope") === "AUTHORITY";
  }
  return (
    member(value, "scope") === "CASE" &&
    fold(member(value, "caseReference")) === fold(member(request, "caseReference")) &&
    successor(member(request, "expectedRevision"), member(value, "revision"))
  );
};
export const correlateReply = (
  endpoint: EndpointId,
  request: unknown,
  response: unknown,
): boolean => {
  if (member(response, "endpoint") !== endpoint) {
    return false;
  }
  const operation = member(request, "operationId");
  const { receiptScope } = endpoints.find((item) => item.id === endpoint)!;
  const scoped = receiptScope !== null;
  if (scoped && member(response, "operationId") !== operation) {
    return false;
  }
  const outcome = member(response, "outcome");
  const data = member(outcome, "data");
  switch (member(outcome, "tag")) {
    case "ACCEPTED":
    case "ALREADY_ACCEPTED":
      return receipt(endpoint, request, data);
    case "OUTCOME_UNKNOWN":
      return member(data, "operationId") === operation;
    case "REVIEWED": {
      const after = member(member(data, "review"), "after");
      return (
        fold(member(after, "caseReference")) === fold(member(request, "caseReference")) &&
        successor(member(request, "expectedRevision"), member(after, "revision"))
      );
    }
    case "SUCCEEDED":
      if (endpoint === "authority.grants") {
        return member(member(data, "actor"), "actorId") === member(request, "actorId");
      }
      if (endpoint === "operation.observe") {
        return member(data, "operationId") === operation;
      }
      return true;
    default:
      return true;
  }
};
