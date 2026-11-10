import { fixtureView } from "./read-view.fixtures";
import { blockedCaseActs } from "./blocked-case-acts.fixtures";
import type { CurrentCase, CaseSummary as Summary } from "../../src/protocol/types";
import type {
  RequesterState_ACTIVE,
  ActorRef,
  CaseStageDisplay,
  CaseView,
  ListedCaseFields,
  Principal,
  Registration,
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
export const initialMaintenance = { active: false } as const;
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
export const actorRefInput = (value: Partial<ActorRef> = {}): ActorRef => ({
  actorId,
  displayName: { kind: "NAMED", value: "Named account" },
  principalKind: "HUMAN",
  availability: "AVAILABLE",
  ...value,
});

type CaseViewSeed = Omit<
  CaseView,
  | "mine"
  | "caseId"
  | "work"
  | "state"
  | "advice"
  | "headline"
  | "attention"
  | "stages"
  | "disposition"
  | "nextActions"
  | "notes"
  | "latestPayments"
  | "changedAt"
  | "recordedBy"
> &
  Partial<
    Pick<
      CaseView,
      | "mine"
      | "caseId"
      | "work"
      | "headline"
      | "attention"
      | "stages"
      | "disposition"
      | "nextActions"
      | "notes"
      | "latestPayments"
      | "changedAt"
      | "recordedBy"
    >
  > & {
    state:
      | LiveStateSeed
      | {
          kind: "ERASED";
        };
    advice: AdviceSeed;
  };
const caseReadMetadata = (
  value: Partial<
    Pick<
      CaseView,
      "disposition" | "nextActions" | "notes" | "latestPayments" | "changedAt" | "recordedBy"
    >
  >,
) => ({
  disposition: value.disposition ?? null,
  nextActions: value.nextActions ?? [],
  notes: value.notes ?? { items: [], count: 0, canAdd: false },
  latestPayments: value.latestPayments ?? [],
  changedAt: value.changedAt ?? "2026-09-01T09:00:00.000000Z",
  recordedBy: value.recordedBy ?? actorRefInput(),
});
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
  kind === "ERASED" ? { kind } : openedStages;
type LiveViewSeed = CaseViewSeed & {
  state: LiveStateSeed;
};
export function caseViewInput(value: LiveViewSeed): Omit<CaseView, "state"> & {
  state: Extract<
    CaseView["state"],
    {
      kind: "ACTIVE" | "VOIDED";
    }
  >;
};
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
    ...caseReadMetadata(value),
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
export const principal = (changes: Partial<Principal> = {}): Principal => ({
  kind: "HUMAN",
  issuer: "https://issuer.example/",
  subject: "synthetic-owner",
  ...changes,
});
// Response seeds are inputs to browser behavior, not generated expectations for product code.
type SummarySeed = Partial<Omit<Summary, "state">> & {
  state?:
    | {
        kind: "ERASED";
      }
    | {
        kind: "ACTIVE" | "VOIDED";
        fields?: ListedCaseFields;
      };
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
    workRevision: 0,
    canAssign: false,
    changedAt: "2026-09-01T12:00:00.000000Z",
    headline: "UNDECIDED",
    attention: [],
    nextAction: null,
    responsible: null,
    pendingReminders: 0,
    paid: null,
    paymentCount: 0,
    lastPaymentDate: null,
    stages: state.kind === "ACTIVE" ? { current: "OPENED", completed: ["OPENED"] } : null,
    ...changes,
    state:
      state.kind === "ERASED"
        ? state
        : { kind: state.kind, fields: state.fields ?? listedFieldsInput },
  };
};
/** Assemble a HUMAN test document from independently authored fixture facts and control offers. */
export const currentCaseInput = (value: LiveViewSeed): CurrentCase => {
  const data = caseViewInput(value);
  const actions = data.advice.acts;
  const canRegister = actions.includes("AMEND_REGISTRATION") || actions.includes("CORRECT");
  const canDecide = actions.includes("DECIDE") || actions.includes("CORRECT");
  const editableInputs: CurrentCase["context"]["editableInputs"] = [
    ...(canRegister
      ? ([
          "field:incidentDate",
          "field:incidentNotificationDate",
          "field:incidentCountry",
          "field:incidentDescription",
          "field:policyNumber",
          "field:insurerClaimReference",
          "field:claimantName",
          "field:insurerName",
          "field:claimedAmount",
          "field:claimedCurrency",
        ] as const)
      : []),
    ...(canDecide
      ? (["field:paymentDecisionDate", "field:payableAmount", "field:payableCurrency"] as const)
      : []),
    ...(data.work.canAssign ? (["field:responsibleActor"] as const) : []),
    ...(data.work.canUpdateSummary ? (["field:handlingSummary"] as const) : []),
  ];
  return {
    screen: fixtureView("case.get", "SUCCEEDED", data),
    context: {
      kind: "CASE",
      caseId: data.caseId,
      revision: data.revision,
      workRevision: data.work.revision,
      history: data.advice.history,
      tasks: [
        ...actions.map((act) => `CASE:${act}` as const),
        ...(data.work.canUpdateSummary || data.work.canAssign
          ? (["WORK:UPDATE_WORK"] as const)
          : []),
        ...(data.work.canCreateReminder ? (["REMINDER:REMINDER_CREATE"] as const) : []),
        ...(data.notes.canAdd ? (["NOTE:ADD_NOTE"] as const) : []),
      ],
      editableInputs,
      correction: data.advice.correction ?? null,
      seeds: {
        registration: canRegister ? data.state.fields.registration : null,
        decision: canDecide ? data.state.fields.finance.decision : null,
        paymentCurrency: actions.includes("RECORD_PAYMENT")
          ? (data.state.fields.finance.decision?.payableCurrency ?? null)
          : null,
        work:
          data.work.canUpdateSummary || data.work.canAssign
            ? {
                summary: data.work.summary,
                responsible: data.work.responsible?.actorId ?? null,
              }
            : null,
      },
      notes: data.notes.items.map((note) => ({ noteId: note.noteId, canRemove: note.canRemove })),
      payments: data.latestPayments.map((payment) => ({
        paymentId: payment.paymentId,
        canRemove: actions.includes("REMOVE_PAYMENT"),
      })),
      nextActions: data.nextActions.map((item) => ({
        caseId: item.reminder.caseId,
        reminderId: item.reminder.reminderId,
        revision: item.reminder.revision,
        tasks: item.acts.map((act) => `REMINDER:REMINDER_${act}` as const),
        actionSeed: item.reminder.action,
        dueDateSeed: item.reminder.dueDate,
      })),
    },
    basis: { kind: "SNAPSHOT", observation: data.work.basis },
    disclosure: "CURRENT",
  };
};
