import { operationAffordance } from "./affordances.fixtures";
import type { HumanReview, HumanReceiptDocument } from "@contracts/client";

const basis = {
  kind: "SNAPSHOT",
  observation: {
    observedAt: "2026-09-09T12:00:00.000000Z",
    businessDate: "2026-09-09",
    timeZone: "Etc/UTC",
  },
} as const;

/** Independently authored documents for delivery state tests; no business DTO is retained. */
export const closeReview: HumanReview = {
  document: {
    screen: { regions: [] },
    subject: {
      kind: "REVIEW",
      confirmation: { kind: "CASE_REFERENCE", reference: "CASE-SERIALIZATION" },
      consequence: "ROUTINE",
    },
    actions: [
      operationAffordance(
        "case.close",
        {
          kind: "CASE",
          target: { kind: "ID", caseId: "20000000-0000-4000-8000-000000000001" },
          expectedRevision: 4,
        },
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
  presentation: { changes: [], unchanged: [] },
};

export const closeReceipt: HumanReceiptDocument = {
  screen: { regions: [] },
  subject: { kind: "RECEIPT", operationId: "10000000-0000-7000-8000-000000000001" },
  actions: [],
  links: [
    {
      rel: "OPEN_CASE",
      labelId: "field.caseReference.label",
      link: {
        endpoint: "case.get",
        input: { target: { kind: "ID", caseId: "20000000-0000-4000-8000-000000000001" } },
      },
    },
    {
      rel: "FOLLOW_UP",
      labelId: "field.caseReference.label",
      link: {
        endpoint: "case.get",
        input: { target: { kind: "ID", caseId: "20000000-0000-4000-8000-000000000001" } },
      },
    },
  ],
  history: null,
  nextCursor: null,
  basis,
  disclosure: "WITHHELD",
};

export const openReview: HumanReview = {
  document: {
    screen: { regions: [] },
    subject: {
      kind: "REVIEW",
      confirmation: { kind: "CASE_REFERENCE", reference: "CASE-1" },
      consequence: "ROUTINE",
    },
    actions: [
      operationAffordance(
        "case.open",
        { kind: "CASE", target: { kind: "REFERENCE", caseReference: "" }, expectedRevision: 0 },
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
  presentation: { changes: [], unchanged: [] },
};
