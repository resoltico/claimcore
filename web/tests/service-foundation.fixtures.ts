import type { Review, CaseFields } from "../src/api/service";

export const caseFields: CaseFields = {
  incidentDate: "2026-09-01",
  incidentNotificationDate: "2026-09-02",
  incidentCountry: "Latvia",
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

export const groupedCorrectionValues = {
  registration: {
    mode: "REPLACE" as const,
    values: {
      incidentDate: "2026-09-01",
      incidentNotificationDate: "2026-09-02",
      incidentCountry: "Latvia",
      claimantName: "Synthetic A",
      insurerName: "Synthetic insurer",
      claimedAmount: "12.34",
      claimedCurrency: "EUR",
    },
  },
  decision: { mode: "KEEP" as const, values: {} },
  payment: { mode: "KEEP" as const, values: {} },
};

export { productMetadata as product } from "../src/generated/contracts/product-metadata";
import { productMetadata } from "../src/generated/contracts/product-metadata";
export const { definition } = productMetadata;
export const operationId = "10000000-0000-4000-8000-000000000001";
export const digest = "a".repeat(64);
export const review: Review = {
  before: null,
  proposed: { revision: "1", fields: caseFields },
  changes: [],
  context: { productVersion: "0.1.0", effectiveBusinessDate: "2026-09-09", timeZoneId: "Etc/UTC" },
  advisory: true,
};
