import { registration } from "./inputs";
import type { OperationReview, CaseFields } from "../../src/api/service";
export const caseFields: CaseFields = {
  caseReference: "CASE-1",
  status: "OPENED",
  registration: registration(),
  decision: null,
  payment: null,
};
export const operationId = "10000000-0000-4000-8000-000000000001";
export const review: OperationReview = {
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
    changes: [],
    context: {
      productVersion: "0.1.0",
      businessDate: "2026-09-09",
      timeZone: "Etc/UTC",
    },
  },
};
