import type { Principal, Registration } from "@contracts/client";

// Build inputs only. Expected outputs and oracle values must stay literal.
export const registration = (changes: Partial<Registration> = {}): Registration => ({
  incidentDate: "2026-09-01",
  incidentNotificationDate: "2026-09-02",
  incidentCountry: "LV",
  claimantName: "Synthetic claimant",
  insurerName: "Synthetic insurer",
  claimedAmount: "12.34",
  claimedCurrency: "EUR",
  ...changes,
});

export const principal = (changes: Partial<Principal> = {}): Principal => ({
  kind: "HUMAN",
  issuer: "https://issuer.example/",
  subject: "synthetic-owner",
  ...changes,
});
