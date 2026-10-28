import type { CaseSummary as Summary } from "../../src/protocol/types";
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

// Response seeds are inputs to browser behavior, not generated expectations for product code.

type LiveSummary = Extract<Summary["state"], { kind: "ACTIVE" | "VOIDED" }>;
type SummarySeed = Partial<Omit<Summary, "state">> & {
  state?: { kind: "ERASED" } | (Pick<LiveSummary, "kind" | "status"> & Partial<LiveSummary>);
};
export const caseSummaryInput = (changes: SummarySeed = {}): Summary => {
  const state = changes.state ?? { kind: "ACTIVE", status: "OPENED" };
  const live: Omit<LiveSummary, "kind" | "status"> = {
    claimantName: "Synthetic claimant",
    insurerName: "Synthetic insurer",
    claimed: { amount: "12.34", currency: "EUR" },
    payable: null,
    progress: "UNDECIDED",
  };
  return {
    caseReference: "SYNTHETIC-001",
    revision: 1,
    changedAt: "2026-09-01T12:00:00.000000Z",
    ...changes,
    state: state.kind === "ERASED" ? state : { ...live, ...state },
  };
};
