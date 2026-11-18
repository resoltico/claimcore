import { registration } from "./inputs";
import type { CaseFields } from "@contracts/client";
export const caseFields: CaseFields = {
  caseReference: "CASE-1",
  handling: { kind: "OPEN" },
  registration: registration(),
  finance: {
    decision: null,
    totals: [],
    paymentCount: 0,
  },
};
export const operationId = "10000000-0000-7000-8000-000000000001";
