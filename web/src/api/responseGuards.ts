import {
  caseActNames,
  authorityActNames,
  hostFailurePolicies,
  type EndpointId,
} from "@contracts/endpoint-catalog";
import { productMetadata } from "@contracts/product-metadata";
import type { HostFailure, ResponseByEndpoint } from "@contracts/types";

const { descriptors } = productMetadata;
export const isObject = (value: unknown): value is Record<string, unknown> =>
  typeof value === "object" && value !== null && !Array.isArray(value);
const text = (value: unknown): value is string => typeof value === "string";
const nullableText = (value: unknown): boolean => value === null || text(value);
const integer = (value: unknown, minimum = 0): value is number =>
  typeof value === "number" && Number.isSafeInteger(value) && value >= minimum;
const uuid = (value: unknown): boolean =>
  text(value) &&
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/u.test(value) &&
  value !== "00000000-0000-0000-0000-000000000000";
const digest = (value: unknown): boolean => text(value) && /^[0-9a-f]{64}$/u.test(value);
const member = (values: ReadonlyArray<string>, value: unknown): boolean =>
  text(value) && values.includes(value);
const list = (value: unknown, item: (value: unknown) => boolean): boolean =>
  Array.isArray(value) && value.every(item);
const position = (value: unknown): boolean =>
  isObject(value) && integer(value["sequence"], 1) && digest(value["entryHash"]);
const diagnostic = (value: unknown, family: "rejection" | "fault"): boolean => {
  if (!isObject(value) || !isObject(value["parameters"])) {
    return false;
  }
  const definitions =
    family === "rejection" ? descriptors.rejectionDiagnostics : descriptors.faultDiagnostics;
  const rule = definitions.find((definition) => definition.id === value["id"]);
  const { parameters } = value;
  return (
    rule !== undefined &&
    Object.keys(parameters).length === rule.parameters.length &&
    rule.parameters.every((parameter) => {
      const current = parameters[parameter.name];
      return integer(current, parameter.minimum) && current <= parameter.maximum;
    })
  );
};
const fields = (value: unknown): boolean =>
  isObject(value) &&
  descriptors.fields.every(
    (field) => (field.allowsAbsence && value[field.name] === null) || text(value[field.name]),
  ) &&
  member(descriptors.statuses, value["status"]);
const disposition = (value: unknown): boolean => value === "ACTIVE" || value === "VOIDED";
const liveState = (value: unknown, advice: boolean): boolean =>
  isObject(value) &&
  value["tag"] === "LIVE" &&
  disposition(value["disposition"]) &&
  fields(value["fields"]) &&
  (!advice || list(value["availableActs"], (act) => member(caseActNames, act)));
const state = (value: unknown, advice: boolean): boolean =>
  isObject(value) && (value["tag"] === "ERASED" || liveState(value, advice));
const snapshot = (value: unknown, advice = false): boolean =>
  isObject(value) &&
  text(value["caseReference"]) &&
  integer(value["revision"], 1) &&
  state(value["state"], advice);
const disclosure = (value: unknown): boolean =>
  isObject(value) &&
  (value["tag"] === "WITHHELD" ||
    value["tag"] === "ERASED" ||
    (value["tag"] === "DISCLOSED" && liveState(value["state"], false)));
const caseIdentity = (value: Record<string, unknown>): boolean =>
  text(value["caseReference"]) &&
  integer(value["revision"], 1) &&
  member(caseActNames, value["act"]);
const receipt = (value: unknown, scope?: "CASE" | "AUTHORITY"): boolean => {
  if (
    !isObject(value) ||
    !uuid(value["operationId"]) ||
    !position(value["position"]) ||
    !text(value["recordedAt"]) ||
    !uuid(value["recordedBy"])
  ) {
    return false;
  }
  if (scope !== undefined && value["scope"] !== scope) {
    return false;
  }
  return value["scope"] === "CASE"
    ? caseIdentity(value) && disclosure(value["snapshot"])
    : value["scope"] === "AUTHORITY" &&
        uuid(value["targetActorId"]) &&
        member(authorityActNames, value["act"]);
};
const summary = (value: unknown): boolean =>
  isObject(value) &&
  text(value["caseReference"]) &&
  integer(value["revision"], 1) &&
  (value["state"] === "ERASED" ||
    (value["state"] === "LIVE" &&
      member(descriptors.statuses, value["status"]) &&
      disposition(value["disposition"])));
const history = (value: unknown): boolean => {
  if (!isObject(value)) {
    return false;
  }
  if (value["tag"] === "FULL") {
    return receipt(value["receipt"], "CASE");
  }
  const { change } = value;
  return (
    value["tag"] === "SUMMARY" &&
    isObject(change) &&
    integer(change["revision"], 1) &&
    member(caseActNames, change["act"]) &&
    uuid(change["operationId"]) &&
    position(change["position"]) &&
    text(change["recordedAt"]) &&
    uuid(change["recordedBy"])
  );
};
const runtime = (value: unknown): boolean =>
  isObject(value) &&
  text(value["productVersion"]) &&
  text(value["businessDate"]) &&
  text(value["timeZone"]);
const review = (value: unknown): boolean =>
  isObject(value) &&
  (value["before"] === null || snapshot(value["before"])) &&
  snapshot(value["after"]) &&
  runtime(value["context"]) &&
  list(
    value["changes"],
    (change) =>
      isObject(change) &&
      descriptors.fields.some((field) => field.name === change["field"]) &&
      nullableText(change["before"]) &&
      nullableText(change["after"]),
  );
const principal = (value: unknown): boolean =>
  isObject(value) &&
  member(["HUMAN", "SERVICE"], value["kind"]) &&
  text(value["issuer"]) &&
  text(value["subject"]);
const actor = (value: unknown): boolean =>
  isObject(value) &&
  uuid(value["actorId"]) &&
  principal(value["principal"]) &&
  typeof value["enabled"] === "boolean" &&
  list(
    value["grants"],
    (grant) =>
      isObject(grant) &&
      descriptors.roles.some((role) => role.role === grant["role"]) &&
      isObject(grant["scope"]) &&
      (grant["scope"]["kind"] === "INSTALLATION" ||
        (grant["scope"]["kind"] === "CASE" && text(grant["scope"]["caseReference"]))),
  );
const page = (value: unknown, key: string, item: (value: unknown) => boolean): boolean =>
  isObject(value) && list(value[key], item) && nullableText(value["nextCursor"]);
const session = (value: unknown): boolean =>
  isObject(value) &&
  typeof value["authenticated"] === "boolean" &&
  digest(value["wireFingerprint"]) &&
  nullableText(value["antiforgeryToken"]);
const actorContext = (value: unknown): boolean =>
  runtime(value) &&
  isObject(value) &&
  list(value["capabilities"], (capability) =>
    descriptors.roles.some((role) => member(role.capabilities, capability)),
  );
const succeeded = (id: EndpointId, value: unknown): boolean => {
  switch (id) {
    case "session":
      return session(value);
    case "session.logout":
      return value === null;
    case "context":
      return actorContext(value);
    case "case.get":
      return snapshot(value, true);
    case "case.list":
      return page(value, "items", summary);
    case "case.history":
      return page(value, "entries", history);
    case "operation.observe":
      return receipt(value);
    case "authority.list":
      return page(value, "items", actor);
    default:
      return false;
  }
};
const action = (value: unknown): boolean =>
  member(
    [
      "CORRECT_INPUT",
      "READ_CURRENT",
      "RETRY_SAFE",
      "REPLAY_EXACT",
      "REAUTHENTICATE",
      "STOP_AND_INVESTIGATE",
      "NONE_REQUIRED",
    ],
    value,
  );
const mutation = (id: EndpointId): boolean => id === "case.execute" || id === "authority.execute";
const scope = (id: EndpointId): "CASE" | "AUTHORITY" =>
  id === "authority.execute" ? "AUTHORITY" : "CASE";
const reviewed = (id: EndpointId, data: unknown): boolean =>
  id === "case.preview" &&
  isObject(data) &&
  review(data["review"]) &&
  digest(data["requestSha256"]);
const unknown = (id: EndpointId, data: unknown): boolean =>
  mutation(id) && isObject(data) && uuid(data["operationId"]) && digest(data["requestSha256"]);
const failed = (data: unknown): boolean =>
  isObject(data) &&
  diagnostic(data["diagnostic"], "fault") &&
  text(data["message"]) &&
  action(data["recommendedAction"]);
const rejected = (data: unknown): boolean =>
  isObject(data) &&
  diagnostic(data["diagnostic"], "rejection") &&
  text(data["message"]) &&
  action(data["recommendedAction"]) &&
  nullableText(data["field"]) &&
  (data["actualRevision"] === null || integer(data["actualRevision"], 1));
const accepted = (id: EndpointId, data: unknown): boolean =>
  mutation(id) && receipt(data, scope(id));
const replay = (id: EndpointId, data: unknown): boolean =>
  (mutation(id) || id === "case.preview") && receipt(data, scope(id));
const outcomeData = (id: EndpointId, tag: unknown, data: unknown): boolean => {
  switch (tag) {
    case "SUCCEEDED":
      return succeeded(id, data);
    case "ACCEPTED":
      return accepted(id, data);
    case "ALREADY_ACCEPTED":
      return replay(id, data);
    case "REVIEWED":
      return reviewed(id, data);
    case "OUTCOME_UNKNOWN":
      return unknown(id, data);
    case "CANCELLED":
      return data === null;
    case "FAILED":
      return failed(data);
    case "REJECTED":
      return rejected(data);
    default:
      return false;
  }
};
export const isResponse = <K extends EndpointId>(
  id: K,
  value: unknown,
): value is ResponseByEndpoint[K] => {
  if (!isObject(value) || value["endpoint"] !== id || !isObject(value["outcome"])) {
    return false;
  }
  const { outcome } = value;
  if ((id === "session" || id === "session.logout") && outcome["tag"] !== "SUCCEEDED") {
    return false;
  }
  return outcomeData(id, outcome["tag"], outcome["data"]);
};
export const isHostFailure = (value: unknown, status: number): value is HostFailure =>
  isObject(value) &&
  value["kind"] === "HOST_FAILURE" &&
  value["status"] === status &&
  text(value["message"]) &&
  isObject(value["diagnostic"]) &&
  isObject(value["diagnostic"]["parameters"]) &&
  Object.keys(value["diagnostic"]["parameters"]).length === 0 &&
  hostFailurePolicies.some(
    (rule) =>
      rule.status === status &&
      rule.code === value["code"] &&
      rule.executionPhase === value["executionPhase"] &&
      isObject(value["diagnostic"]) &&
      rule.diagnosticId === value["diagnostic"]["id"],
  );
