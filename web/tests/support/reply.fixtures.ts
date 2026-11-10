import { caseDocumentInput } from "./case-document.fixtures";
import type {
  EndpointId,
  HumanReceiptDocument,
  HumanResponseByEndpoint,
  HumanReview,
  Screen,
} from "@contracts/client";
import { consumerContractIdentity } from "@contracts/client";
import { principal, workBasis } from "./inputs";

import { activeRequester } from "./human-requester.fixtures";
import { endpointResponse } from "./transport-response.fixtures";

const operationId = "10000000-0000-7000-8000-000000000001";
const caseId = "20000000-0000-4000-8000-000000000001";
const actorId = "10000000-0000-4000-8000-000000000099";
const empty: Screen = { regions: [] };
const basis = { kind: "SNAPSHOT", observation: workBasis } as const;
const succeeded = <T>(data: T) => ({ outcome: { kind: "SUCCEEDED" as const, data, view: empty } });
const read = <const C>(context: C) => ({
  representation: "HUMAN" as const,
  outcome: {
    kind: "SUCCEEDED" as const,
    document: { screen: empty, context, basis, disclosure: "CURRENT" as const },
  },
});
const caseReceipt = (): HumanReceiptDocument => ({
  screen: empty,
  context: { kind: "RECEIPT", target: { kind: "CASE", caseId, revision: 2 }, refreshCase: caseId },
  basis,
  disclosure: "CURRENT",
});
export const authorityReceipt = (): HumanReceiptDocument => ({
  screen: empty,
  context: { kind: "RECEIPT", target: { kind: "AUTHORITY", actorId }, refreshCase: null },
  basis,
  disclosure: "CURRENT",
});
const caseReview: HumanReview = {
  document: {
    screen: empty,
    context: {
      kind: "REVIEW",
      task: "CASE:CLOSE",
      target: { kind: "CASE", caseId, beforeRevision: 1 },
      inputs: ["input:operationId", "field:caseReference", "input:expectedRevision"],
      consequence: "ROUTINE",
      confirmation: { kind: "CASE_REFERENCE", reference: "CASE-1" },
    },
    basis,
    disclosure: "CURRENT",
  },
  presentation: { changes: [], unchanged: [] },
};
const authorityReview: HumanReview = {
  document: {
    screen: empty,
    context: {
      kind: "REVIEW",
      task: "AUTHORITY:GRANT",
      target: { kind: "AUTHORITY", actorId, scope: { kind: "INSTALLATION" } },
      inputs: [
        "input:operationId",
        "input:principal.kind",
        "input:principal.issuer",
        "input:principal.subject",
        "input:role",
        "input:scope",
        "field:caseReference",
      ],
      consequence: "ROUTINE",
      confirmation: { kind: "OPERATION", operationId },
    },
    basis,
    disclosure: "CURRENT",
  },
  presentation: { changes: [], unchanged: [] },
};
type Subject = "CASE" | "AUTHORITY";
type Replies = { [K in EndpointId]: (subject: Subject) => HumanResponseByEndpoint[K] };
const replies: Replies = {
  session: () =>
    succeeded({
      authenticated: true,
      contractIdentity: consumerContractIdentity,
      antiforgeryToken: "csrf",
    }),
  "session.logout": () => succeeded(null),
  context: () => ({
    representation: "HUMAN",
    outcome: {
      kind: "SUCCEEDED",
      document: {
        ...activeRequester,
        context: {
          ...activeRequester.context,
          tasks: [
            "CASE:OPEN",
            "AUTHORITY:REGISTER",
            "AUTHORITY:ADMIT",
            "AUTHORITY:GRANT",
            "AUTHORITY:REVOKE",
            "AUTHORITY:ENABLE",
            "AUTHORITY:DISABLE",
            "query:authorityList",
            "query:authorityGrants",
            "query:authorityHistory",
            "query:authorityRequests",
          ],
        },
      },
    },
  }),
  "case.get": () => ({
    representation: "HUMAN",
    outcome: {
      kind: "SUCCEEDED",
      document: caseDocumentInput({ tasks: [], caseId, revision: 1, history: "NONE" }),
    },
  }),
  "case.list": () =>
    read({ kind: "CASES", nextCursor: null, tasks: [], cases: [], exactCaseId: null }),
  "case.summary": () => read({ kind: "EMPTY" }),
  "case.assignees": () => read({ kind: "ASSIGNEES", actorIds: [], nextCursor: null }),
  "case.notes": () => read({ kind: "NOTE_PAGE", caseId, notes: [], tasks: [], nextCursor: null }),
  "case.payments": () =>
    read({ kind: "PAYMENT_PAGE", caseId, revision: 1, payments: [], nextCursor: null }),
  "case.timeline": () => read({ kind: "PAGE", tasks: [], nextCursor: null }),
  "work.views": () => read({ kind: "WORK_VIEWS", cases: [], reminders: [] }),
  "reminder.get": () =>
    read({
      kind: "REMINDER",
      caseId,
      reminderId: operationId,
      revision: 1,
      tasks: [],
      actionSeed: null,
      dueDateSeed: null,
    }),
  "reminder.list": () => read({ kind: "REMINDERS", reminders: [], nextCursor: null }),
  "authority.list": () => read({ kind: "PEOPLE", actorIds: [], nextCursor: null }),
  "authority.grants": () =>
    read({
      kind: "PERSON",
      actorId,
      principal: principal(),
      profileSeed: null,
      revocations: [],
      responsibleCases: null,
      tasks: [],
      nextCursor: null,
    }),
  "authority.history": () => read({ kind: "AUTHORITY_HISTORY", entries: [], nextCursor: null }),
  "authority.requests": () => read({ kind: "ACCESS_REQUESTS", requests: [], nextCursor: null }),
  "authority.requests.dismiss": () => read({ kind: "EMPTY" }),
  "operation.execute": (subject) => ({
    representation: "HUMAN",
    operationId,
    outcome: {
      kind: "ACCEPTED",
      document: subject === "CASE" ? caseReceipt() : authorityReceipt(),
    },
  }),
  "operation.preview": (subject) => ({
    representation: "HUMAN",
    operationId,
    outcome: { kind: "REVIEWED", review: subject === "CASE" ? caseReview : authorityReview },
  }),
  "operation.get": () => ({
    representation: "HUMAN",
    operationId,
    outcome: { kind: "ACCEPTED", document: caseReceipt(), byRequester: true },
  }),
};
/** Authored HUMAN families; each specialized behavior supplies its own screen and control facts. */
export const wireReply = <K extends EndpointId>(
  endpoint: K,
  subject: Subject = "CASE",
): HumanResponseByEndpoint[K] => structuredClone(replies[endpoint](subject));
export const wireResponse = (
  endpoint: EndpointId,
  status = 200,
  subject: Subject = "CASE",
): Response => endpointResponse(endpoint, wireReply(endpoint, subject), status);
