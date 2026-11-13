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
  context: {
    kind: "RECEIPT",
    recordedFields: [],
    target: { kind: "CASE", caseId, revision: 2 },
    caseLink: caseId,
    refreshCase: caseId,
  },
  basis,
  disclosure: "CURRENT",
});
export const peopleReceipt = (): HumanReceiptDocument => ({
  screen: empty,
  context: {
    kind: "RECEIPT",
    recordedFields: [],
    target: { kind: "PEOPLE", actorId },
    caseLink: null,
    refreshCase: null,
  },
  basis,
  disclosure: "CURRENT",
});
const caseReview: HumanReview = {
  document: {
    screen: empty,
    context: {
      kind: "REVIEW",
      task: "case.close",
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
const peopleReview: HumanReview = {
  document: {
    screen: empty,
    context: {
      kind: "REVIEW",
      task: "people.grant",
      target: { kind: "PEOPLE", actorId, scope: { kind: "INSTALLATION" } },
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
type Subject = "CASE" | "PEOPLE";
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
            "case.open",
            "people.register",
            "people.admit",
            "people.grant",
            "people.revoke",
            "people.enable",
            "people.disable",
            "people.list",
            "people.grants",
            "people.history",
            "people.requests",
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
  "people.list": () => read({ kind: "PEOPLE", actorIds: [], nextCursor: null }),
  "people.grants": () =>
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
  "people.history": () => read({ kind: "PEOPLE_HISTORY", entries: [], nextCursor: null }),
  "people.requests": () => read({ kind: "ACCESS_REQUESTS", requests: [], nextCursor: null }),
  "people.requests.dismiss": () => read({ kind: "EMPTY" }),
  "operation.execute": (subject) => ({
    representation: "HUMAN",
    operationId,
    outcome: {
      kind: "ACCEPTED",
      document: subject === "CASE" ? caseReceipt() : peopleReceipt(),
    },
  }),
  "operation.preview": (subject) => ({
    representation: "HUMAN",
    operationId,
    outcome: { kind: "REVIEWED", review: subject === "CASE" ? caseReview : peopleReview },
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
