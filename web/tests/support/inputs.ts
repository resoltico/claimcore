import type { Principal, Registration } from "@contracts/client";

import { inventory } from "./inventory";

// Build inputs only. Expected outputs and oracle values must stay literal.
const samples: Record<string, string> = {
  DATE: "2026-09-01",
  COUNTRY: "LV",
  NAME: "Synthetic claimant",
  AMOUNT: "12.34",
  CURRENCY: "EUR",
};

export const registration = (changes: Partial<Registration> = {}): Registration => ({
  ...(Object.fromEntries(
    inventory.groups.registration.map(({ key, kind }) => [key, samples[kind]]),
  ) as Registration),
  incidentNotificationDate: "2026-09-02",
  insurerName: "Synthetic insurer",
  ...changes,
});

export const principal = (changes: Partial<Principal> = {}): Principal => ({
  kind: "HUMAN",
  issuer: "https://issuer.example/",
  subject: "synthetic-owner",
  ...changes,
});
