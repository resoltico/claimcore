import { registration } from "./inputs";
import type { CaseFields } from "../../src/api/service";
export const caseFields: CaseFields = {
  caseReference: "CASE-1",
  status: "OPENED",
  registration: registration(),
  finance: {
    decision: null,
    paidAmount: "0",
    paymentCount: 0,
    firstPaymentDate: null,
    lastPaymentDate: null,
  },
};
export const operationId = "10000000-0000-7000-8000-000000000001";
