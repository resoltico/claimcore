import { fixtureView } from "./read-view.fixtures";
import type {
  EndpointId,
  OperationReceipt,
  ResponseByEndpoint,
  ReminderReadModel,
} from "@contracts/client";
import { consumerContractIdentity } from "@contracts/client";
import { actorContextInput, actorRefInput, caseViewInput, principal, workBasis } from "./inputs";
import { caseFields, review } from "./service-foundation.fixtures";
import { workViewsFixture } from "./view-counts.fixtures";
import { timelinePage } from "./timeline.fixtures";
import { transportResponse } from "./transport-response.fixtures";

const operationId = "10000000-0000-7000-8000-000000000001";
const caseId = "20000000-0000-4000-8000-000000000001";
const position = { sequence: 1, entryHash: "a".repeat(64) };
const actor = () => actorRefInput({ displayName: { kind: "NAMED", value: "Synthetic recorder" } });
const succeeded = <T>(data: T) => ({
  outcome: { view: { regions: [] }, kind: "SUCCEEDED" as const, data },
});
const caseReceipt = (): OperationReceipt => ({
  operationId,
  position,
  recordedAt: workBasis.observedAt,
  recordedBy: actor(),
  basis: workBasis,
  subject: {
    kind: "CASE",
    caseId: operationId,
    caseReference: caseFields.caseReference,
    revision: 1,
    act: "OPEN",
    snapshot: { kind: "DISCLOSED", view: { kind: "ACTIVE", fields: caseFields } },
  },
});
export const authorityReceipt = (): OperationReceipt => ({
  operationId,
  position,
  recordedAt: workBasis.observedAt,
  recordedBy: actor(),
  basis: workBasis,
  subject: {
    kind: "AUTHORITY",
    act: "PROVISION_OWNER",
    targetActor: {
      kind: "DISCLOSED",
      actor: actorRefInput({
        actorId: "10000000-0000-4000-8000-000000000099",
        displayName: { kind: "NAMED", value: "Synthetic target" },
      }),
    },
    effects: [{ act: "PROVISION_OWNER", position }],
  },
});
const reminder = (): ReminderReadModel => ({
  reminder: {
    reminderId: operationId,
    caseId,
    revision: 1,
    action: "Contact claimant",
    dueDate: workBasis.businessDate,
    state: "PENDING",
  },
  caseReference: caseFields.caseReference,
  recordedAt: workBasis.observedAt,
  recordedBy: actor(),
  acts: [],
  active: true,
  timing: "TODAY",
});
type Subject = "CASE" | "AUTHORITY";
type Replies = { [K in EndpointId]: (subject: Subject) => ResponseByEndpoint[K] };
const replies: Replies = {
  session: () =>
    succeeded({
      authenticated: true,
      contractIdentity: consumerContractIdentity,
      antiforgeryToken: "csrf",
    }),
  "session.logout": () => succeeded(null),
  context: () =>
    succeeded({
      requester: actorContextInput({
        productVersion: "synthetic",
        observedAt: workBasis.observedAt,
        businessDate: workBasis.businessDate,
        timeZone: workBasis.timeZone,
        capabilities: [
          "READ_CASE",
          "READ_HISTORY",
          "READ_HISTORY_FULL",
          "REGISTER",
          "DECIDE",
          "RECORD_PAYMENT",
          "CLOSE",
          "STEWARD",
          "MANAGE_AUTHORITY",
          "READ_AUTHORITY",
        ],
        views: ["CASES", "AUTHORITY", "OPERATIONS"],
        offers: [
          { kind: "OPEN" },
          { kind: "AUTHORITY_ACT", act: "REGISTER" },
          { kind: "AUTHORITY_ACT", act: "GRANT" },
          { kind: "AUTHORITY_ACT", act: "REVOKE" },
          { kind: "AUTHORITY_ACT", act: "ENABLE" },
          { kind: "AUTHORITY_ACT", act: "DISABLE" },
        ],
      }),
    }),
  "case.get": () =>
    succeeded(
      caseViewInput({
        caseId,
        caseReference: caseFields.caseReference,
        revision: 1,
        state: { kind: "ACTIVE", fields: caseFields },
        advice: { acts: [], history: "NONE" },
      }),
    ),
  "case.list": () =>
    succeeded({ basis: workBasis, items: [], nextCursor: null, offers: [], exactReference: null }),
  "case.summary": () =>
    succeeded({
      basis: workBasis,
      counts: { total: 0, lifecycle: [], status: [], progress: [], assignment: [], due: [] },
      claimed: [],
      payable: [],
    }),
  "case.assignees": () => succeeded({ basis: workBasis, items: [], nextCursor: null }),
  "case.timeline": () => succeeded(timelinePage()),
  "work.views": () => succeeded(workViewsFixture),
  "reminder.get": () => succeeded({ basis: workBasis, item: reminder() }),
  "reminder.list": () => succeeded({ basis: workBasis, items: [], nextCursor: null }),
  "authority.list": () => succeeded({ basis: workBasis, items: [], nextCursor: null }),
  "authority.grants": () =>
    succeeded({
      basis: workBasis,
      actor: {
        actor: actor(),
        principal: principal(),
        contact: null,
        state: "ACTIVE" as const,
        roles: [],
      },
      items: [],
      nextCursor: null,
    }),
  "authority.history": () => succeeded({ basis: workBasis, entries: [], nextCursor: null }),
  "authority.requests": () => succeeded({ basis: workBasis, items: [], nextCursor: null }),
  "authority.requests.dismiss": () => succeeded(null),
  "operation.execute": (subject) => ({
    operationId,
    outcome: {
      view: { regions: [] },
      kind: "ACCEPTED",
      data: subject === "CASE" ? caseReceipt() : authorityReceipt(),
    },
  }),
  "operation.preview": () => ({ operationId, outcome: { kind: "REVIEWED", data: { review } } }),
  "operation.get": () => ({
    operationId,
    outcome: {
      view: { regions: [] },
      kind: "ACCEPTED",
      data: { receipt: caseReceipt(), byRequester: true },
    },
  }),
};
/** Behavioral inputs are typed builders; literal decoding examples have a separate reader. */
export const wireReply = <K extends EndpointId>(
  endpoint: K,
  subject: Subject = "CASE",
): ResponseByEndpoint[K] => {
  const reply = structuredClone(replies[endpoint](subject));
  if ("view" in reply.outcome) {
    Object.assign(reply.outcome, {
      view: fixtureView(endpoint, reply.outcome.kind, reply.outcome.data),
    });
  }
  return reply;
};
export const wireResponse = (
  endpoint: EndpointId,
  status = 200,
  subject: Subject = "CASE",
): Response => {
  const value = wireReply(endpoint, subject);
  return transportResponse(
    endpoint,
    value.outcome.kind,
    value.outcome.data,
    status,
    "view" in value.outcome ? value.outcome.view : undefined,
  );
};
