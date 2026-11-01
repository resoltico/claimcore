import type { CaseSummary as Summary } from "../../src/protocol/types";
import type {
  ActorContext,
  CaseView,
  Principal,
  Registration,
  Decision,
  Payment,
  CaseFields,
  CaseWorkView,
  WorkDateBasis,
} from "@contracts/client";

import { inventory } from "./inventory";

export const workBasis: WorkDateBasis = {
  observedAt: "2026-09-01T12:00:00.000000Z",
  businessDate: "2026-09-01",
  timeZone: "Etc/UTC",
};
export const emptyCaseWork = { responsibleActor: null, followUpDate: null } as const;
export const unscheduledWork: CaseWorkView = {
  responsible: null,
  followUpDate: null,
  due: "UNSCHEDULED",
  basis: workBasis,
};
type LiveStateSeed = {
  kind: "ACTIVE" | "VOIDED";
  fields: Omit<CaseFields, "work"> & Partial<Pick<CaseFields, "work">>;
};
type CaseViewSeed = Omit<CaseView, "mine" | "caseId" | "work" | "state"> &
  Partial<Pick<CaseView, "mine" | "caseId" | "work">> & {
    state: LiveStateSeed | { kind: "ERASED" };
  };
type LiveViewSeed = CaseViewSeed & { state: LiveStateSeed };
export function caseViewInput(
  value: LiveViewSeed,
): Omit<CaseView, "state"> & { state: Extract<CaseView["state"], { kind: "ACTIVE" | "VOIDED" }> };
export function caseViewInput(value: CaseViewSeed): CaseView;
export function caseViewInput(value: CaseViewSeed): CaseView {
  const state =
    value.state.kind === "ERASED"
      ? value.state
      : {
          ...value.state,
          fields: { ...value.state.fields, work: value.state.fields.work ?? emptyCaseWork },
        };
  const active = state.kind === "ACTIVE" && state.fields.status === "OPENED";
  return {
    ...value,
    state,
    work: value.work ?? { ...unscheduledWork, due: active ? "UNSCHEDULED" : "INACTIVE" },
    mine: value.mine ?? false,
    caseId: value.caseId ?? "20000000-0000-4000-8000-000000000001",
  };
}
type ActorContextSeed = Omit<ActorContext, "hasScopedCases" | "actorId" | "installationId"> &
  Partial<Pick<ActorContext, "hasScopedCases" | "actorId" | "installationId">>;
export const actorContextInput = <T extends ActorContextSeed>(value: T) => ({
  ...value,
  hasScopedCases: value.hasScopedCases ?? false,
  actorId: value.actorId ?? inventory.cliSamples.actorId,
  installationId: value.installationId ?? "90000000-0000-4000-8000-000000000001",
});

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

export const decision = (changes: Partial<Decision> = {}): Decision => ({
  ...(Object.fromEntries(
    inventory.groups.decision.map(({ key, kind }) => [key, samples[kind]]),
  ) as Decision),
  ...changes,
});
export const payment = (changes: Partial<Payment> = {}): Payment => ({
  ...(Object.fromEntries(
    inventory.groups.payment.map(({ key, kind }) => [key, samples[kind]]),
  ) as Payment),
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
    work: {
      ...unscheduledWork,
      due: state.kind === "ACTIVE" && state.status === "OPENED" ? "UNSCHEDULED" : "INACTIVE",
    },
    claimantName: "Synthetic claimant",
    insurerName: "Synthetic insurer",
    claimed: { amount: "12.34", currency: "EUR" },
    payable: null,
    progress: "UNDECIDED",
    incidentNotificationDate: "2026-09-02",
  };
  return {
    caseId: "20000000-0000-4000-8000-000000000001",
    caseReference: "SYNTHETIC-001",
    revision: 1,
    changedAt: "2026-09-01T12:00:00.000000Z",
    ...changes,
    state: state.kind === "ERASED" ? state : { ...live, ...state },
  };
};

const isObject = (value: unknown): value is Record<string, unknown> =>
  typeof value === "object" && value !== null && !Array.isArray(value);
type Group = keyof typeof inventory.groups;
const isGroup = (key: string): key is Group => Object.hasOwn(inventory.groups, key);
const groupInput = (group: Group): Record<string, unknown> =>
  ({ registration, decision, payment })[group]();
const payloadGroup = (group: Group | undefined, kind: unknown): Group | undefined => {
  if (kind === "REPLACE") {
    return group;
  }
  const payload = inventory.caseActs.find(({ token }) => token === kind)?.payload.toLowerCase();
  return payload !== undefined && isGroup(payload) ? payload : undefined;
};
// Authored positive examples override builder defaults. Unknown members remain for strict admission.
const childGroup = (key: string, payload: Group | undefined): Group | undefined => {
  if (key === "values") {
    return payload;
  }
  return isGroup(key) ? key : undefined;
};
const completeRead = (completed: Record<string, unknown>): unknown => {
  if (
    Object.hasOwn(completed, "registration") &&
    Object.hasOwn(completed, "status") &&
    Object.hasOwn(completed, "caseReference")
  ) {
    return { work: emptyCaseWork, ...completed };
  }
  if (
    Object.hasOwn(completed, "items") &&
    Object.hasOwn(completed, "offers") &&
    Object.hasOwn(completed, "nextCursor")
  ) {
    return { basis: workBasis, ...completed };
  }
  if (
    Object.hasOwn(completed, "counts") &&
    Object.hasOwn(completed, "claimed") &&
    Object.hasOwn(completed, "payable")
  ) {
    const { counts } = completed;
    return {
      basis: workBasis,
      ...completed,
      counts: isObject(counts) ? { assignment: [], due: [], ...counts } : counts,
    };
  }
  return completed;
};
export const positiveValue = (seed: unknown): unknown => {
  const complete = (value: unknown, group?: Group): unknown => {
    if (Array.isArray(value)) {
      return value.map((child: unknown) => complete(child));
    }
    if (!isObject(value)) {
      return value;
    }
    const payload = payloadGroup(group, value["kind"]);
    const members = Object.fromEntries(
      Object.entries(value).map(([key, child]) => [key, complete(child, childGroup(key, payload))]),
    );
    const completed =
      group !== undefined && value["kind"] === undefined
        ? { ...groupInput(group), ...members }
        : members;
    return completeRead(completed);
  };
  return complete(seed);
};
