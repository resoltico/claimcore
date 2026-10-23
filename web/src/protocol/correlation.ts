import type { EndpointId } from "@contracts/client";

const object = (value: unknown): value is Record<string, unknown> =>
  typeof value === "object" && value !== null && !Array.isArray(value);
const at = (value: unknown, path: ReadonlyArray<string>): unknown =>
  path.reduce<unknown>((current, name) => (object(current) ? current[name] : undefined), value);
const reference = (expected: unknown, actual: unknown): boolean =>
  typeof expected === "string" &&
  typeof actual === "string" &&
  /^[!-~]{1,80}$/u.test(expected) &&
  /^[!-~]{1,80}$/u.test(actual) &&
  expected.toUpperCase() === actual.toUpperCase();
const successor = (expected: unknown, actual: unknown): boolean =>
  typeof expected === "number" &&
  expected >= 0 &&
  Number.isSafeInteger(expected + 1) &&
  actual === expected + 1;
const receipt = (request: unknown, value: unknown): boolean => {
  const scope = at(request, ["scope"]);
  return (
    at(request, ["operationId"]) === at(value, ["operationId"]) &&
    scope === at(value, ["scope", "kind"]) &&
    at(request, ["act", "kind"]) === at(value, ["scope", "act"]) &&
    (scope === "AUTHORITY" ||
      (scope === "CASE" &&
        reference(at(request, ["caseReference"]), at(value, ["scope", "caseReference"])) &&
        successor(at(request, ["expectedRevision"]), at(value, ["scope", "revision"]))))
  );
};

const review = (request: unknown, data: unknown): boolean => {
  const scope = at(request, ["scope"]);
  return (
    scope === at(data, ["review", "scope"]) &&
    (scope === "AUTHORITY" ||
      (scope === "CASE" &&
        reference(at(request, ["caseReference"]), at(data, ["review", "after", "caseReference"])) &&
        successor(at(request, ["expectedRevision"]), at(data, ["review", "after", "revision"]))))
  );
};
/** Schema admission precedes these request-dependent consistency checks. */
export const correlateReply = (
  endpoint: EndpointId,
  request: unknown,
  response: unknown,
): boolean => {
  const tag = at(response, ["outcome", "tag"]);
  const data = at(response, ["outcome", "data"]);
  if (endpoint === "operation.execute" || endpoint === "operation.preview") {
    const id = at(request, ["operationId"]);
    if (typeof id !== "string" || id !== at(response, ["operationId"])) {
      return false;
    }
    if (tag === "ACCEPTED" || tag === "ALREADY_ACCEPTED") {
      return receipt(request, data);
    }
    if (tag === "REVIEWED") {
      return review(request, data);
    }
  }
  if (tag === "SUCCEEDED" && endpoint === "operation.observe") {
    return at(request, ["operationId"]) === at(data, ["operationId"]);
  }
  if (tag === "SUCCEEDED" && endpoint === "authority.grants") {
    return at(request, ["actorId"]) === at(data, ["actor", "actorId"]);
  }
  return true;
};
