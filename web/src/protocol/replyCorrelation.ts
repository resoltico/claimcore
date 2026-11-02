import { isObject } from "./client";
import type { EndpointId } from "@contracts/client";
const object = (value: unknown): Record<string, unknown> => (isObject(value) ? value : {});
const identity = (value: unknown): string | null =>
  typeof value === "string" ? value.toLowerCase() : null;
const reference = (value: unknown): string | null =>
  typeof value === "string"
    ? value.replace(/[a-z]/gu, (character) => character.toUpperCase())
    : null;
const sameIdentity = (left: unknown, right: unknown): boolean =>
  identity(left) !== null && identity(left) === identity(right);
const positiveRevision = (value: unknown): value is number =>
  typeof value === "number" && Number.isSafeInteger(value) && value > 0;
const nextRevision = (expected: unknown, actual: unknown): boolean =>
  typeof expected === "number" &&
  Number.isSafeInteger(expected) &&
  expected >= 0 &&
  positiveRevision(actual) &&
  actual === expected + 1;
const caseTargetMatches = (
  request: Record<string, unknown>,
  actual: Record<string, unknown>,
): boolean => {
  const selector = object(request["target"]);
  if (selector["kind"] === "REFERENCE") {
    const expected = reference(selector["caseReference"]);
    return expected !== null && expected === reference(actual["caseReference"]);
  }
  return selector["kind"] === "ID" && sameIdentity(selector["caseId"], actual["caseId"]);
};
const snapshotMatches = (
  version: Record<string, unknown>,
  parent: unknown,
  state?: string,
): boolean => {
  const disclosure = object(version["snapshot"]);
  if (disclosure["kind"] !== "DISCLOSED") {
    return disclosure["kind"] === "WITHHELD" || disclosure["kind"] === "ERASED";
  }
  const value = object(disclosure["view"]);
  return (
    sameIdentity(value["caseId"], parent) &&
    sameIdentity(value["reminderId"], version["reminderId"]) &&
    value["revision"] === version["revision"] &&
    (state === undefined || value["state"] === state)
  );
};
const reminderStateAfter = (act: unknown): string | undefined => {
  switch (act) {
    case "CREATE":
    case "UPDATE":
    case "REOPEN":
      return "PENDING";
    case "COMPLETE":
    case "COMPLETE_AND_PLAN_NEXT":
      return "COMPLETED";
    case "CANCEL":
      return "CANCELLED";
    default:
      return undefined;
  }
};
const coherentWorkReceipt = (subject: Record<string, unknown>): boolean => {
  const snapshot = object(subject["snapshot"]);
  const value = object(snapshot["view"]);
  if (!positiveRevision(subject["revision"])) {
    return false;
  }
  if (snapshot["kind"] !== "DISCLOSED") {
    return snapshot["kind"] === "WITHHELD" || snapshot["kind"] === "ERASED";
  }
  return (
    sameIdentity(subject["caseId"], value["caseId"]) && value["revision"] === subject["revision"]
  );
};
const coherentCompound = (
  receipt: Record<string, unknown>,
  subject: Record<string, unknown>,
  effects: Record<string, unknown>,
): boolean => {
  const completed = object(effects["completed"]);
  const next = object(effects["next"]);
  return (
    subject["act"] === "COMPLETE_AND_PLAN_NEXT" &&
    positiveRevision(completed["revision"]) &&
    next["revision"] === 1 &&
    sameIdentity(next["reminderId"], receipt["operationId"]) &&
    !sameIdentity(completed["reminderId"], next["reminderId"]) &&
    snapshotMatches(completed, subject["caseId"], "COMPLETED") &&
    snapshotMatches(next, subject["caseId"], "PENDING")
  );
};
const coherentSingle = (
  receipt: Record<string, unknown>,
  subject: Record<string, unknown>,
  effects: Record<string, unknown>,
): boolean => {
  const version = object(effects["reminder"]);
  if (
    effects["kind"] !== "SINGLE" ||
    subject["act"] === "COMPLETE_AND_PLAN_NEXT" ||
    !positiveRevision(version["revision"]) ||
    !snapshotMatches(version, subject["caseId"], reminderStateAfter(subject["act"]))
  ) {
    return false;
  }
  return (
    subject["act"] !== "CREATE" ||
    (version["revision"] === 1 && sameIdentity(version["reminderId"], receipt["operationId"]))
  );
};
/** Permanent bounded effect relationships are independent of generated structural schemas. */
const coherentReceipt = (receipt: Record<string, unknown>): boolean => {
  const subject = object(receipt["subject"]);
  if (subject["kind"] === "WORK") {
    return coherentWorkReceipt(subject);
  }
  if (subject["kind"] !== "REMINDER") {
    return true;
  }
  const effects = object(subject["effects"]);
  return effects["kind"] === "COMPLETED_AND_PLANNED"
    ? coherentCompound(receipt, subject, effects)
    : coherentSingle(receipt, subject, effects);
};
const receiptEffectMatches = (
  request: Record<string, unknown>,
  subject: Record<string, unknown>,
): boolean => {
  if (request["kind"] === "AUTHORITY") {
    return true;
  }
  if (!caseTargetMatches(request, subject)) {
    return false;
  }
  if (request["kind"] === "CASE" || request["kind"] === "WORK") {
    return nextRevision(request["expectedRevision"], subject["revision"]);
  }
  const act = object(request["act"]);
  const effects = object(subject["effects"]);
  const changed = object(effects[effects["kind"] === "SINGLE" ? "reminder" : "completed"]);
  return (
    act["kind"] === "CREATE" ||
    (sameIdentity(act["reminderId"], changed["reminderId"]) &&
      nextRevision(act["expectedRevision"], changed["revision"]))
  );
};
/** Frozen source correlation also applies to a passive status answer before clearing its journal. */
export const receiptCorrelates = (input: unknown, value: unknown): boolean => {
  const request = object(input);
  const receipt = object(value);
  if (!sameIdentity(request["operationId"], receipt["operationId"]) || !coherentReceipt(receipt)) {
    return false;
  }
  if (request["kind"] === undefined) {
    return true;
  }
  const subject = object(receipt["subject"]);
  const act = object(request["act"]);
  if (subject["kind"] !== request["kind"] || subject["act"] !== act["kind"]) {
    return false;
  }
  return receiptEffectMatches(request, subject);
};
const receiptMatches = (
  endpoint: EndpointId,
  request: Record<string, unknown>,
  outcome: Record<string, unknown>,
): boolean =>
  (!["ACCEPTED", "ALREADY_ACCEPTED"].includes(String(outcome["kind"])) &&
    !(endpoint === "operation.observe" && outcome["kind"] === "SUCCEEDED")) ||
  receiptCorrelates(request, outcome["data"]);
const reminderPageTargetMatches = (
  request: Record<string, unknown>,
  data: Record<string, unknown>,
): boolean => {
  if (request["target"] === undefined) {
    return true;
  }
  const { items } = data;
  return (
    Array.isArray(items) &&
    items.every((item: unknown) => {
      const row = object(item);
      return caseTargetMatches(request, {
        ...object(row["reminder"]),
        caseReference: row["caseReference"],
      });
    })
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
  if (endpoint === "case.get" || endpoint === "case.history" || endpoint === "case.activity") {
    return caseTargetMatches(request, data);
  }
  if (endpoint === "work.overview") {
    return data["mine"] === (request["mine"] === true);
  }
  if (endpoint === "reminder.list") {
    return reminderPageTargetMatches(request, data);
  }
  return (
    endpoint !== "authority.grants" ||
    sameIdentity(request["actorId"], object(data["actor"])["actorId"])
  );
};
const previewWorkMatches = (
  request: Record<string, unknown>,
  subject: Record<string, unknown>,
): boolean => {
  const before = object(subject["before"]);
  const proposed = object(subject["proposed"]);
  return (
    caseTargetMatches(request, { ...proposed, caseReference: subject["caseReference"] }) &&
    before["revision"] === request["expectedRevision"] &&
    nextRevision(before["revision"], proposed["revision"]) &&
    sameIdentity(before["caseId"], proposed["caseId"])
  );
};
const createdReminderMatches = (
  request: Record<string, unknown>,
  subject: Record<string, unknown>,
  effects: Record<string, unknown>,
  after: Record<string, unknown>,
): boolean =>
  subject["before"] === null &&
  effects["kind"] === "CREATED" &&
  sameIdentity(after["reminderId"], request["operationId"]) &&
  after["revision"] === 1 &&
  after["state"] === "PENDING" &&
  sameIdentity(after["caseId"], subject["caseId"]);
const changedReminderMatches = (
  act: Record<string, unknown>,
  subject: Record<string, unknown>,
  before: Record<string, unknown>,
  after: Record<string, unknown>,
): boolean =>
  sameIdentity(before["reminderId"], act["reminderId"]) &&
  before["revision"] === act["expectedRevision"] &&
  sameIdentity(after["reminderId"], act["reminderId"]) &&
  nextRevision(before["revision"], after["revision"]) &&
  sameIdentity(after["caseId"], subject["caseId"]) &&
  after["state"] === reminderStateAfter(act["kind"]);
const previewCompoundMatches = (
  request: Record<string, unknown>,
  subject: Record<string, unknown>,
  effects: Record<string, unknown>,
  after: Record<string, unknown>,
): boolean => {
  const next = object(effects["next"]);
  return (
    effects["kind"] === "COMPLETED_AND_PLANNED" &&
    sameIdentity(next["reminderId"], request["operationId"]) &&
    !sameIdentity(next["reminderId"], after["reminderId"]) &&
    next["revision"] === 1 &&
    next["state"] === "PENDING" &&
    sameIdentity(next["caseId"], subject["caseId"])
  );
};
const previewReminderMatches = (
  request: Record<string, unknown>,
  subject: Record<string, unknown>,
): boolean => {
  if (!caseTargetMatches(request, subject)) {
    return false;
  }
  const act = object(request["act"]);
  const effects = object(subject["effects"]);
  const before = object(subject["before"]);
  const after = object(
    effects[effects["kind"] === "COMPLETED_AND_PLANNED" ? "completed" : "reminder"],
  );
  if (act["kind"] === "CREATE") {
    return createdReminderMatches(request, subject, effects, after);
  }
  if (!changedReminderMatches(act, subject, before, after)) {
    return false;
  }
  return act["kind"] === "COMPLETE_AND_PLAN_NEXT"
    ? previewCompoundMatches(request, subject, effects, after)
    : effects["kind"] === "CHANGED";
};
const previewTargetMatches = (
  endpoint: EndpointId,
  request: Record<string, unknown>,
  outcome: Record<string, unknown>,
): boolean => {
  if (endpoint !== "operation.preview" || outcome["kind"] !== "REVIEWED") {
    return true;
  }
  const subject = object(object(object(outcome["data"])["review"])["subject"]);
  if (subject["kind"] !== request["kind"]) {
    return false;
  }
  switch (request["kind"]) {
    case "AUTHORITY":
      return true;
    case "CASE":
      return caseTargetMatches(request, object(subject["after"]));
    case "WORK":
      return previewWorkMatches(request, subject);
    case "REMINDER":
      return previewReminderMatches(request, subject);
    default:
      return false;
  }
};
/** Structural decoding is a prerequisite; these are transport identities, not business rules. */
export const replyCorrelates = (endpoint: EndpointId, input: unknown, reply: unknown): boolean => {
  const request = object(input);
  const envelope = object(reply);
  const outcome = object(envelope["outcome"]);
  const operation = [
    "operation.preview",
    "operation.execute",
    "operation.observe",
    "operation.status",
    "operation.resolve",
  ].includes(endpoint);
  return (
    (!operation || sameIdentity(request["operationId"], envelope["operationId"])) &&
    receiptMatches(endpoint, request, outcome) &&
    readTargetMatches(endpoint, request, outcome) &&
    previewTargetMatches(endpoint, request, outcome)
  );
};
