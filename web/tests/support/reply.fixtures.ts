import { installationActions, requesterLinks, activeRequester } from "./human-requester.fixtures";
import {
  type DocumentSubject,
  type EndpointId,
  type HumanReceiptDocument,
  type HumanResponseByEndpoint,
  type HumanReview,
  type Screen,
  consumerContractIdentity,
} from "@contracts/client";
import { caseSubject, caseDocumentInput } from "./case-document.fixtures";
import { operationAffordance } from "./affordances.fixtures";

import { workBasis } from "./inputs";

import { endpointResponse } from "./transport-response.fixtures";
const operationId = "10000000-0000-7000-8000-000000000001";
const caseId = "20000000-0000-4000-8000-000000000001";
const actorId = "10000000-0000-4000-8000-000000000099";
const empty: Screen = {
  regions: [],
};
const basis = {
  kind: "SNAPSHOT",
  observation: workBasis,
} as const;
const succeeded = <T>(data: T) => ({
  outcome: {
    kind: "SUCCEEDED" as const,
    data,
    view: empty,
  },
});
const read = <const C extends DocumentSubject>(subject: C) => ({
  representation: "HUMAN" as const,
  outcome: {
    kind: "SUCCEEDED" as const,
    document: {
      screen: empty,
      subject,
      actions: [],
      links: [],
      nextCursor: null,
      history: null,
      basis,
      disclosure: "CURRENT" as const,
    },
  },
});
const caseReceipt = (): HumanReceiptDocument => ({
  screen: empty,
  subject: { kind: "RECEIPT", operationId: "10000000-0000-7000-8000-000000000001" },
  actions: [],
  links: [
    {
      rel: "OPEN_CASE",
      labelId: "field.caseReference.label",
      link: { endpoint: "case.get", input: { target: { kind: "ID", caseId } } },
    },
    {
      rel: "FOLLOW_UP",
      labelId: "field.caseReference.label",
      link: { endpoint: "case.get", input: { target: { kind: "ID", caseId } } },
    },
  ],
  history: null,
  nextCursor: null,
  basis,
  disclosure: "CURRENT",
});
export const peopleReceipt = (): HumanReceiptDocument => ({
  screen: empty,
  subject: { kind: "RECEIPT", operationId: "10000000-0000-7000-8000-000000000001" },
  actions: [],
  links: [],
  history: null,
  nextCursor: null,
  basis,
  disclosure: "CURRENT",
});
const caseReview: HumanReview = {
  document: {
    screen: empty,
    subject: {
      kind: "REVIEW",
      confirmation: {
        kind: "CASE_REFERENCE",
        reference: "CASE-1",
      },
      consequence: "ROUTINE",
    },
    actions: [
      operationAffordance(
        "case.close",
        { kind: "CASE", target: { kind: "ID", caseId }, expectedRevision: 1 },
        {},
        [],
      ),
    ],
    links: [],
    history: null,
    nextCursor: null,
    basis,
    disclosure: "CURRENT",
  },
  presentation: {
    changes: [],
    unchanged: [],
  },
};
const peopleReview: HumanReview = {
  document: {
    screen: empty,
    subject: {
      kind: "REVIEW",
      confirmation: {
        kind: "OPERATION",
        operationId,
      },
      consequence: "ROUTINE",
    },
    actions: [operationAffordance("people.grant", { kind: "PEOPLE", actorId }, {}, [])],
    links: [],
    history: null,
    nextCursor: null,
    basis,
    disclosure: "CURRENT",
  },
  presentation: {
    changes: [],
    unchanged: [],
  },
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
        actions: installationActions([
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
        ]),
        links: requesterLinks(
          ["CASES", "PEOPLE", "OPERATIONS"],
          [
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
        ),
      },
    },
  }),
  "case.get": () => ({
    representation: "HUMAN",
    outcome: {
      kind: "SUCCEEDED",
      document: caseDocumentInput({
        subject: { ...caseSubject, caseId, revision: 1 },
        actions: [],
        history: "NONE",
      }),
    },
  }),
  "case.list": () => read({ kind: "LIST" }),
  "case.summary": () => read({ kind: "LIST" }),
  "case.assignees": () => read({ kind: "LIST" }),
  "case.notes": () => read({ kind: "LIST" }),
  "case.payments": () => read({ kind: "CASE", caseId, revision: 1, workRevision: 0 }),
  "case.timeline": () => read({ kind: "LIST" }),
  "work.views": () => read({ kind: "LIST" }),
  "reminder.get": () => read({ kind: "LIST" }),
  "reminder.list": () => read({ kind: "LIST" }),
  "people.list": () => read({ kind: "LIST" }),
  "people.grants": () => read({ kind: "PERSON", actorId }),
  "people.history": () => read({ kind: "LIST" }),
  "people.requests": () => read({ kind: "LIST" }),
  "people.requests.dismiss": () => read({ kind: "LIST" }),
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
    outcome: {
      kind: "REVIEWED",
      review: subject === "CASE" ? caseReview : peopleReview,
    },
  }),
  "operation.get": () => ({
    representation: "HUMAN",
    operationId,
    outcome: {
      kind: "ACCEPTED",
      document: caseReceipt(),
      byRequester: true,
    },
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
