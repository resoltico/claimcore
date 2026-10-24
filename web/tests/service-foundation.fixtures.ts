import type { CaseReview, CaseFields } from "../src/api/service";

export const caseFields: CaseFields = {
  incidentDate: "2026-09-01",
  incidentNotificationDate: "2026-09-02",
  incidentCountry: "LV",
  claimantName: "Synthetic claimant",
  insurerName: "Synthetic insurer",
  claimedAmount: "12.34",
  claimedCurrency: "EUR",
  caseReference: "CASE-1",
  paymentDecisionDate: null,
  payableAmount: null,
  payableCurrency: null,
  paymentDate: null,
  status: "OPENED",
};

export const operationId = "10000000-0000-4000-8000-000000000001";
export const review: CaseReview = {
  scope: "CASE",
  before: null,
  after: {
    caseReference: caseFields.caseReference,
    revision: 1,
    state: { tag: "LIVE", disposition: "ACTIVE", fields: caseFields },
  },
  changes: [],
  context: { productVersion: "0.1.0", businessDate: "2026-09-09", timeZone: "Etc/UTC" },
};
