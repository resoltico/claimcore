import { registration } from "./inputs";
import type { OperationReview, CaseFields } from "../../src/api/service";
export const caseFields: CaseFields = {
  caseReference: "CASE-1",
  status: "OPENED",
  progress: "UNDECIDED",
  registration: registration(),
  decision: null,
  payments: [],
};
export const operationId = "10000000-0000-7000-8000-000000000001";
export const review: OperationReview = {
  view: { regions: [] },
  presentation: { changes: [], unchanged: [] },
  subject: {
    kind: "CASE",
    before: null,
    after: {
      caseId: "20000000-0000-4000-8000-000000000001",
      caseReference: caseFields.caseReference,
      revision: 1,
      state: {
        kind: "ACTIVE",
        fields: caseFields,
      },
    },
    context: {
      productVersion: "0.1.0",
      observedAt: "2026-09-09T12:00:00.000000Z",
      businessDate: "2026-09-09",
      timeZone: "Etc/UTC",
    },
  },
};
