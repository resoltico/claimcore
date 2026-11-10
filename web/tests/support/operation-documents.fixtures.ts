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
    context: {
      kind: "REVIEW",
      task: "CASE:CLOSE",
      target: { kind: "CASE", caseId: "20000000-0000-4000-8000-000000000001", beforeRevision: 4 },
      inputs: [],
      consequence: "ROUTINE",
      confirmation: { kind: "CASE_REFERENCE", reference: "CASE-SERIALIZATION" },
    },
    basis,
    disclosure: "CURRENT",
  },
  presentation: { changes: [], unchanged: [] },
};

export const closeReceipt: HumanReceiptDocument = {
  screen: { regions: [] },
  context: {
    kind: "RECEIPT",
    target: { kind: "CASE", caseId: "20000000-0000-4000-8000-000000000001", revision: 5 },
    refreshCase: "20000000-0000-4000-8000-000000000001",
  },
  basis,
  disclosure: "WITHHELD",
};
