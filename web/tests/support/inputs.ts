import { fixtureView } from "./read-view.fixtures";
import { isCaseAct } from "../../src/operation/metadata";
import { blockedCaseActs } from "./blocked-case-acts.fixtures";
import type { CurrentCase, CaseSummary as Summary } from "../../src/protocol/types";
import type {
  RequesterState_ACTIVE,
  ActorRef,
  CaseStageDisplay,
  CaseStageOutline,
  CaseView,
  ListedCaseFields,
  Principal,
  Registration,
  Decision,
  Payment,
  CaseFields,
  CaseWorkView,
  WorkDateBasis,
} from "@contracts/client";

export const actorId = "10000000-0000-4000-8000-000000000001";

export const workBasis: WorkDateBasis = {
  observedAt: "2026-09-01T12:00:00.000000Z",
  businessDate: "2026-09-01",
  timeZone: "Etc/UTC",
};
export const initialMaintenance = { active: false, generation: 0 } as const;
const unscheduledWork: CaseWorkView = {
  changedAt: null,
  recordedBy: null,
  revision: 0,
  summary: null,
  summaryChangedAt: null,
  summaryRecordedBy: null,
  nextAction: null,
  pendingReminders: 0,
  overdueReminders: 0,
  needsNextAction: true,
  canUpdateSummary: false,
  canAssign: false,
  canCreateReminder: false,
  responsible: null,
  followUpDate: null,
  due: "UNSCHEDULED",
  basis: workBasis,
};
type LiveStateSeed = {
  kind: "ACTIVE" | "VOIDED";
  fields: CaseFields;
};
type AdviceSeed = Omit<CaseView["advice"], "maintenance" | "recommended" | "blocked"> &
  Partial<Pick<CaseView["advice"], "maintenance" | "recommended" | "blocked">>;
type CaseViewSeed = Omit<
  CaseView,
  "mine" | "caseId" | "work" | "state" | "advice" | "headline" | "attention" | "stages"
> &
  Partial<Pick<CaseView, "mine" | "caseId" | "work" | "headline" | "attention" | "stages">> & {
    state: LiveStateSeed | { kind: "ERASED" };
    advice: AdviceSeed;
  };
// This read-model fixture represents an opened case. Stage-specific tests supply literal projections.
const openedStages: CaseStageDisplay = {
  kind: "TRACKER",
  current: "OPENED",
  steps: [
    {
      stage: "OPENED",
      completed: true,
      date: "2026-09-01",
      amount: { amount: "12.34", currency: "EUR" },
    },
    { stage: "DECIDED", completed: false, date: null, amount: null },
    { stage: "PAID", completed: false, date: null, amount: null },
    { stage: "CLOSED", completed: false, date: null, amount: null },
  ],
};
const stageDisplayInput = (kind: unknown): CaseStageDisplay =>
  kind === "VOIDED" || kind === "ERASED" ? { kind } : openedStages;
type LiveViewSeed = CaseViewSeed & { state: LiveStateSeed };
export function caseViewInput(
  value: LiveViewSeed,
): Omit<CaseView, "state"> & { state: Extract<CaseView["state"], { kind: "ACTIVE" | "VOIDED" }> };
export function caseViewInput(value: CaseViewSeed): CaseView;
export function caseViewInput(value: CaseViewSeed): CaseView {
  const { state } = value;
  const active = state.kind === "ACTIVE" && state.fields.status === "OPENED";
  return {
    caseReference: value.caseReference,
    revision: value.revision,
    advice: {
      recommended: null,
      blocked: blockedCaseActs(
        value.advice.acts,
        value.headline ?? "UNDECIDED",
        value.advice.maintenance?.active,
      ),
      ...value.advice,
      maintenance: value.advice.maintenance ?? initialMaintenance,
    },
    headline: value.headline ?? "UNDECIDED",
    attention: value.attention ?? [],
    stages: value.stages ?? stageDisplayInput(state.kind),
    state,
    work: value.work ?? {
      ...unscheduledWork,
      due: active ? "UNSCHEDULED" : "INACTIVE",
      needsNextAction: active,
    },
    mine: value.mine ?? false,
    caseId: value.caseId ?? "20000000-0000-4000-8000-000000000001",
  };
}
export const actorRefInput = (value: Partial<ActorRef> = {}): ActorRef => ({
  actorId,
  displayName: { kind: "NAMED", value: "Named account" },
  principalKind: "HUMAN",
  availability: "AVAILABLE",
  ...value,
});

type RequesterState_ACTIVESeed = Omit<
  RequesterState_ACTIVE,
  | "hasScopedCases"
  | "actorId"
  | "installationId"
  | "maintenance"
  | "self"
  | "kind"
  | "observedAt"
  | "issuer"
  | "roles"
> &
  Partial<
    Pick<
      RequesterState_ACTIVE,
      | "hasScopedCases"
      | "actorId"
      | "installationId"
      | "maintenance"
      | "self"
      | "kind"
      | "observedAt"
      | "issuer"
      | "roles"
    >
  >;
export const actorContextInput = <T extends RequesterState_ACTIVESeed>(value: T) => ({
  ...value,
  observedAt: value.observedAt ?? `${value.businessDate}T12:00:00.000000Z`,
  issuer: value.issuer ?? "https://issuer.example/",
  roles: value.roles ?? [],
  kind: "ACTIVE" as const,
  self: value.self ?? actorRefInput(),
  maintenance: value.maintenance ?? initialMaintenance,
  hasScopedCases: value.hasScopedCases ?? false,
  actorId: value.actorId ?? actorId,
  installationId: value.installationId ?? "90000000-0000-4000-8000-000000000001",
});

// Build inputs only. Expected outputs and oracle values must stay literal.
export const registration = (
  changes: Partial<Registration> = {},
): Registration & CaseFields["registration"] => ({
  incidentDate: "2026-09-01",
  incidentNotificationDate: "2026-09-02",
  incidentCountry: "LV",
  claimantName: "Synthetic claimant",
  insurerName: "Synthetic insurer",
  claimedAmount: "12.34",
  claimedCurrency: "EUR",
  incidentDescription: null,
  ...changes,
});
const decision = (changes: Partial<Decision> = {}): Decision => ({
  paymentDecisionDate: "2026-09-01",
  payableAmount: "12.34",
  payableCurrency: "EUR",
  ...changes,
});
export const payment = (changes: Partial<Payment> = {}): Payment => ({
  paymentDate: "2026-09-01",
  ...changes,
});

export const principal = (changes: Partial<Principal> = {}): Principal => ({
  kind: "HUMAN",
  issuer: "https://issuer.example/",
  subject: "synthetic-owner",
  ...changes,
});

// Response seeds are inputs to browser behavior, not generated expectations for product code.

type SummarySeed = Partial<Omit<Summary, "state">> & {
  state?: { kind: "ERASED" } | { kind: "ACTIVE" | "VOIDED"; fields?: ListedCaseFields };
};
const listedFieldsInput: ListedCaseFields = {
  claimantName: "Synthetic claimant",
  insurerName: "Synthetic insurer",
  claimedAmount: "12.34",
  claimedCurrency: "EUR",
  payableAmount: null,
  payableCurrency: null,
};
export const caseSummaryInput = (changes: SummarySeed = {}): Summary => {
  const state = changes.state ?? { kind: "ACTIVE" };
  return {
    caseId: "20000000-0000-4000-8000-000000000001",
    caseReference: "SYNTHETIC-001",
    revision: 1,
    changedAt: "2026-09-01T12:00:00.000000Z",
    headline: "UNDECIDED",
    attention: [],
    nextAction: null,
    responsible: null,
    pendingReminders: 0,
    stages: state.kind === "ACTIVE" ? { current: "OPENED", completed: ["OPENED"] } : null,
    ...changes,
    state:
      state.kind === "ERASED"
        ? state
        : { kind: state.kind, fields: state.fields ?? listedFieldsInput },
  };
};

const isObject = (value: unknown): value is Record<string, unknown> =>
  typeof value === "object" && value !== null && !Array.isArray(value);
type Group = "registration" | "decision" | "payment";
const isGroup = (key: string): key is Group =>
  Object.hasOwn({ registration, decision, payment }, key);
const groupInput = (group: Group): Record<string, unknown> =>
  ({ registration, decision, payment })[group]();
const payloadGroup = (group: Group | undefined, kind: unknown): Group | undefined => {
  if (kind === "REPLACE") {
    return group;
  }
  if (kind === "OPEN") {
    return "registration";
  }
  if (kind === "DECIDE") {
    return "decision";
  }
  if (kind === "RECORD_PAYMENT") {
    return "payment";
  }
  return undefined;
};
// Authored positive examples override builder defaults. Unknown members remain for strict admission.
const childGroup = (key: string, payload: Group | undefined): Group | undefined => {
  if (key === "values") {
    return payload;
  }
  return isGroup(key) ? key : undefined;
};
const isAdviceOrContext = (value: Record<string, unknown>): boolean =>
  (Object.hasOwn(value, "acts") && Object.hasOwn(value, "history")) ||
  (Object.hasOwn(value, "installationId") && Object.hasOwn(value, "capabilities"));
const isCaseReadSeed = (value: Record<string, unknown>): boolean =>
  ["state", "caseReference", "revision"].every((key) => Object.hasOwn(value, key)) &&
  (Object.hasOwn(value, "advice") || Object.hasOwn(value, "changedAt"));
const completeCaseRead = (completed: Record<string, unknown>) => {
  const { state } = completed;
  const kind = isObject(state) ? state["kind"] : undefined;
  let stages: CaseStageDisplay | CaseStageOutline | null = stageDisplayInput(kind);
  if (Object.hasOwn(completed, "changedAt")) {
    stages = kind === "ACTIVE" ? { current: "OPENED", completed: ["OPENED"] } : null;
  }
  const data = { headline: "UNDECIDED", attention: [], stages, ...completed };
  return Object.hasOwn(completed, "changedAt")
    ? { nextAction: null, responsible: null, pendingReminders: 0, ...data }
    : data;
};
const completeAuthorityRead = (completed: Record<string, unknown>): unknown => {
  if (
    Object.hasOwn(completed, "actor") &&
    Object.hasOwn(completed, "principal") &&
    Object.hasOwn(completed, "contact")
  ) {
    const { actor } = completed;
    return {
      state: isObject(actor) && actor["availability"] === "DISABLED" ? "DISABLED" : "NO_ROLES",
      roles: [],
      ...completed,
    };
  }
  if (
    completed["kind"] === "AUTHORITY" &&
    Object.hasOwn(completed, "before") &&
    Object.hasOwn(completed, "after") &&
    Object.hasOwn(completed, "accessScope")
  ) {
    return { basis: workBasis, effects: [], ...completed };
  }
  return null;
};
const adviceActs = (value: unknown) => (Array.isArray(value) ? value.filter(isCaseAct) : []);
const completeObservation = (
  completed: Record<string, unknown>,
): Record<string, unknown> | null => {
  if (
    Object.hasOwn(completed, "productVersion") &&
    Object.hasOwn(completed, "businessDate") &&
    Object.hasOwn(completed, "timeZone") &&
    !Object.hasOwn(completed, "capabilities")
  ) {
    return { observedAt: `${String(completed["businessDate"])}T12:00:00.000000Z`, ...completed };
  }
  if (
    !Object.hasOwn(completed, "detail") &&
    ["operationId", "recordedAt", "recordedBy", "subject"].every((key) =>
      Object.hasOwn(completed, key),
    )
  ) {
    return { basis: workBasis, ...completed };
  }
  return null;
};
const completeAdvice = (completed: Record<string, unknown>): Record<string, unknown> | null => {
  if (isAdviceOrContext(completed)) {
    return {
      maintenance: initialMaintenance,
      ...(Object.hasOwn(completed, "capabilities")
        ? {
            observedAt: `${String(completed["businessDate"])}T12:00:00.000000Z`,
            issuer: "https://issuer.example/",
            roles: [],
          }
        : {}),
      ...(Object.hasOwn(completed, "acts")
        ? {
            recommended: null,
            blocked: blockedCaseActs(adviceActs(completed["acts"])),
          }
        : {}),
      ...completed,
    };
  }
  return null;
};
const completeRead = (completed: Record<string, unknown>): unknown => {
  const observed = completeObservation(completed);
  if (observed !== null) {
    return observed;
  }
  const advice = completeAdvice(completed);
  if (advice !== null) {
    return advice;
  }
  const authority = completeAuthorityRead(completed);
  if (authority !== null) {
    return authority;
  }
  if (isCaseReadSeed(completed)) {
    return completeCaseRead(completed);
  }

  if (
    Object.hasOwn(completed, "items") &&
    Object.hasOwn(completed, "offers") &&
    Object.hasOwn(completed, "nextCursor")
  ) {
    return { basis: workBasis, exactReference: null, ...completed };
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

/** An editor observation includes the independently authored wire presentation. */
export const currentCaseInput = (value: LiveViewSeed): CurrentCase => ({
  ...caseViewInput(value),
  view: fixtureView("case.get", "SUCCEEDED", caseViewInput(value)),
});
