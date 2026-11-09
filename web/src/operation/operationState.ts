import { sameDateEntryDraft, withoutDateEntries } from "./dateEntryDraft";
import { isCurrentCase } from "../protocol/types";
import { conflictFromRefusal, sameConflictTarget } from "./operationConflict";
import { roleAdmitted } from "./roleAvailability";
import { operationEntry } from "../protocol/operationCatalog";
import { taskForAct } from "../protocol/taskCatalog";
import { inputErrors } from "./inputErrors";
import {
  operationSource,
  type EffectIdentity,
  type OperationPhase,
  type OperationEffect,
  type OperationState,
  type OperationAction,
} from "./operationTypes";
export type {
  EffectIdentity,
  OperationEffect,
  OperationState,
  OperationAction,
} from "./operationTypes";
const operationBusy = (state: OperationState): boolean =>
  state.operation.kind === "PREVIEWING" ||
  state.operation.kind === "PREPARING" ||
  state.operation.kind === "SUBMITTING" ||
  (state.operation.kind === "UNRESOLVED" && state.operation.effect !== null);
const canEdit = (state: OperationState): boolean =>
  state.operation.kind === "EDITING" || state.operation.kind === "REVIEWING";
const current = (state: OperationState, identity: EffectIdentity): boolean =>
  state.context.generation === identity.contextGeneration &&
  state.context.credential === identity.credential &&
  state.context.target === identity.target;
const completing = (state: OperationState, identity: EffectIdentity): boolean =>
  current(state, identity) &&
  operationBusy(state) &&
  (state.operation.kind === "PREVIEWING" ||
    state.operation.kind === "PREPARING" ||
    state.operation.kind === "SUBMITTING" ||
    (state.operation.kind === "UNRESOLVED" && state.operation.effect !== null)) &&
  state.operation.effect?.id === identity.id;
const append = (state: OperationState, effect: OperationEffect): OperationState => ({
  ...state,
  effects: [...state.effects, effect],
});
const localSource = operationSource;
const uncertain = (phase: OperationPhase): boolean => phase.kind === "UNRESOLVED";
type EditAction = Extract<OperationAction, { type: "EDIT" }>;
const clearsRoleSelection = (state: OperationState, action: EditAction): boolean => {
  if (
    state.entry.subject !== "AUTHORITY" ||
    state.entry.kind !== "GRANT" ||
    (action.path !== "input:principal.kind" && action.path !== "input:scope")
  ) {
    return false;
  }
  const values = { ...state.values, [action.path]: action.value };
  const role = values["input:role"];
  const principal = values["input:principal.kind"];
  const scope = values["input:scope"];
  return (
    typeof role === "string" &&
    typeof principal === "string" &&
    !roleAdmitted(role, principal, typeof scope === "string" ? scope : undefined)
  );
};
const edit = (state: OperationState, action: EditAction): OperationState => {
  if (!canEdit(state)) {
    return state;
  }
  const dateEntries = withoutDateEntries(state.dateEntries, [action.path]);
  if (state.values[action.path] === action.value) {
    return dateEntries === state.dateEntries ? state : { ...state, dateEntries };
  }
  return {
    ...state,
    dateEntries,
    generation: state.generation + 1,
    operation: {
      kind: "EDITING",
      exposed: null,
    },
    values: {
      ...state.values,
      [action.path]: action.value,
      ...(clearsRoleSelection(state, action) ? { "input:role": "" } : {}),
    },
    notice: null,
    fieldErrors: {},
    rejection: null,
  };
};
/** Fact selection and money-pair removal are one authoring transition. */
const editFields = (
  state: OperationState,
  action: Extract<OperationAction, { type: "EDIT_FIELDS" }>,
): OperationState => {
  if (!canEdit(state)) {
    return state;
  }
  const values = { ...state.values, ...action.values };
  for (const path of action.omitted) {
    delete values[path];
  }
  const changed =
    action.omitted.some((path) => Object.hasOwn(state.values, path)) ||
    Object.entries(action.values).some(([path, value]) => state.values[path] !== value);
  const dateEntries = withoutDateEntries(state.dateEntries, [
    ...Object.keys(action.values),
    ...action.omitted,
  ]);
  if (!changed) {
    return dateEntries === state.dateEntries ? state : { ...state, dateEntries };
  }
  return {
    ...state,
    dateEntries,
    generation: state.generation + 1,
    operation: { kind: "EDITING", exposed: null },
    values,
    notice: null,
    fieldErrors: {},
    rejection: null,
  };
};
const disclosureRevision = (state: OperationState, refused: boolean) =>
  state.disclosureRevision + (refused ? 1 : 0);
const invalidate = (state: OperationState, refused: boolean): OperationState => {
  const phase = state.operation;
  let operation: OperationPhase;
  if (phase.kind === "SUBMITTING" || phase.kind === "UNRESOLVED") {
    operation = {
      kind: "UNRESOLVED",
      source: phase.source,
      effect: null,
    };
  } else if (phase.kind === "ACCEPTED") {
    operation = {
      ...phase,
      receipt: null,
    };
  } else {
    operation = {
      kind: "EDITING",
      exposed: localSource(phase),
    };
  }
  return {
    ...state,
    operation,
    disclosureRefused: state.disclosureRefused || refused,
    disclosureRevision: disclosureRevision(state, refused),
    disclosureRead: null,
    effects: [],
  };
};
const preview = (
  state: OperationState,
  action: Extract<
    OperationAction,
    {
      type: "PREVIEW";
    }
  >,
): OperationState => {
  if (
    !canEdit(state) ||
    state.conflict !== null ||
    !current(state, action.identity) ||
    state.generation !== action.generation ||
    state.disclosureRead !== null
  ) {
    return state;
  }
  const source = action.source;
  return append(
    {
      ...state,
      operation: {
        kind: "PREVIEWING",
        source,
        effect: action.identity,
      },
      notice: null,
      fieldErrors: {},
      rejection: null,
    },
    {
      kind: "PREVIEW",
      identity: action.identity,
      source,
    },
  );
};
const execute = (state: OperationState, identity: EffectIdentity): OperationState => {
  const phase = state.operation;
  if (!current(state, identity)) {
    return state;
  }
  const reviewed =
    phase.kind === "REVIEWING" &&
    phase.confirmed &&
    !state.disclosureRefused &&
    state.disclosureContext === state.context;
  if (operationBusy(state) || (phase.kind !== "UNRESOLVED" && !reviewed)) {
    return state;
  }
  const source = operationSource(phase);
  if (source === null) {
    return state;
  }
  return append(
    {
      ...state,
      operation:
        phase.kind === "UNRESOLVED"
          ? {
              ...phase,
              effect: identity,
            }
          : {
              kind: "PREPARING",
              source,
              effect: identity,
              review: phase.kind === "REVIEWING" ? phase.review : null,
            },
      notice: null,
    },
    {
      kind: "EXECUTE",
      identity,
      source,
    },
  );
};
const readConflict = (
  state: OperationState,
  action: Extract<OperationAction, { type: "READ_CONFLICT" }>,
) => {
  const snapshot = state.conflict;
  if (
    snapshot === null ||
    snapshot.identity.id !== action.conflictId ||
    !current(state, action.identity) ||
    operationBusy(state) ||
    uncertain(state.operation) ||
    state.disclosureRead !== null
  ) {
    return state;
  }
  return append(
    {
      ...state,
      conflict: { ...snapshot, latest: null },
      caseObservation: null,
      disclosureRead: action.identity,
      notice: null,
    },
    { kind: "READ_CONFLICT", identity: action.identity, snapshot },
  );
};
const conflictObservation = (
  state: OperationState,
  action: Extract<OperationAction, { type: "CONFLICT_OBSERVED" | "CONFLICT_READ_FAILED" }>,
): OperationState => {
  const snapshot = state.conflict;
  if (
    snapshot === null ||
    snapshot.identity.id !== action.conflictId ||
    state.disclosureRead?.id !== action.identity.id ||
    !current(state, action.identity)
  ) {
    return state;
  }
  if (action.type === "CONFLICT_READ_FAILED") {
    return {
      ...state,
      disclosureRead: null,
      notice: action.notice,
      disclosureRefused: state.disclosureRefused || action.refused,
      disclosureRevision: state.disclosureRevision + 1,
    };
  }
  return {
    ...state,
    conflict: { ...snapshot, latest: action.value },
    caseObservation: isCurrentCase(action.value.current) ? action.value.current : null,
    disclosureRead: null,
    disclosureContext: state.context,
    disclosureRefused: false,
    notice: null,
  };
};
const reviewLatest = (
  state: OperationState,
  action: Extract<OperationAction, { type: "REVIEW_LATEST" }>,
): OperationState => {
  const snapshot = state.conflict;
  if (
    snapshot === null ||
    snapshot.identity.id !== action.conflictId ||
    snapshot.latest === null ||
    snapshot.latest.revision === null ||
    !canEdit(state) ||
    !current(state, action.identity) ||
    state.generation !== action.generation ||
    state.disclosureRead !== null ||
    state.disclosureRefused
  ) {
    return state;
  }
  if (!sameConflictTarget(snapshot.source.prepared.input, action.source.prepared.input)) {
    return state;
  }
  const source = action.source;
  return append(
    {
      ...state,
      values: action.values,
      generation: state.generation + 1,
      conflict: null,
      notice: null,
      fieldErrors: {},
      rejection: null,
      operation: { kind: "PREVIEWING", source, effect: action.identity },
    },
    { kind: "PREVIEW", source, identity: action.identity },
  );
};
const refuse = (
  state: OperationState,
  action: Extract<
    OperationAction,
    {
      type: "REFUSE";
    }
  >,
): OperationState => {
  if (action.identity !== undefined && !completing(state, action.identity)) {
    return state;
  }
  const source = operationSource(state.operation);
  const sticky = uncertain(state.operation) || action.uncertain;
  const retained = source;
  const operation: OperationPhase =
    sticky && retained !== null
      ? {
          kind: "UNRESOLVED",
          source: retained,
          effect: null,
        }
      : {
          kind: "EDITING",
          exposed: retained,
        };
  const next = {
    ...state,
    operation,
    notice: action.notice,
    fieldErrors: inputErrors(
      action.rejection,
      taskForAct(state.entry.subject, state.entry.kind),
      source?.submittedRows ?? {},
      action.target,
      action.notice,
    ),
    rejection: action.rejection ?? null,
    disclosureRefused: state.disclosureRefused || action.disclosure,
    disclosureRevision: disclosureRevision(state, action.disclosure),
    conflict:
      conflictFromRefusal(action, source, operationEntry(state.entry), sticky) ?? state.conflict,
  };
  if (next.conflict !== null && next.conflict !== state.conflict) {
    return readConflict(next, {
      type: "READ_CONFLICT",
      identity: next.conflict.identity,
      conflictId: next.conflict.identity.id,
    });
  }
  return action.disclosure && action.identity !== undefined
    ? append(next, {
        kind: "DISCLOSURE_REFUSED",
        identity: action.identity,
        notice: action.notice,
      })
    : next;
};
const record = (
  state: OperationState,
  action: Extract<
    OperationAction,
    {
      type: "RECORD";
    }
  >,
): OperationState => {
  if (state.operation.kind === "PREPARING" || !completing(state, action.identity)) {
    return state;
  }
  const source = operationSource(state.operation);
  if (source === null) {
    return state;
  }
  return append(
    {
      ...state,
      operation: {
        kind: "ACCEPTED",
        source,
        receipt: action.receipt,
      },
      pendingDiscard: null,
      dateEntries: {},
      notice: null,
      disclosureContext: state.context,
      disclosureRefused: false,
    },
    {
      kind: "RECORDED",
      identity: action.identity,
      receipt: action.receipt,
    },
  );
};
const reviewed = (
  state: OperationState,
  action: Extract<
    OperationAction,
    {
      type: "REVIEW";
    }
  >,
): OperationState => {
  if (!completing(state, action.identity) || state.operation.kind !== "PREVIEWING") {
    return state;
  }
  return {
    ...state,
    operation: {
      kind: "REVIEWING",
      source: state.operation.source,
      review: action.review,
      confirmed: false,
    },
    disclosureContext: state.context,
    disclosureRefused: false,
  };
};
const contextChanged = (
  state: OperationState,
  action: Extract<
    OperationAction,
    {
      type: "CONTEXT";
    }
  >,
): OperationState =>
  state.context.credential === action.credential &&
  state.context.target === action.target &&
  state.context.epoch === action.epoch
    ? state
    : {
        ...invalidate(state, false),
        conflict: null,
        context: {
          generation: state.context.generation + 1,
          epoch: action.epoch,
          credential: action.credential,
          target: action.target,
        },
      };
const returned = (state: OperationState): OperationState =>
  canEdit(state)
    ? {
        ...state,
        operation: {
          kind: "EDITING",
          exposed: localSource(state.operation),
        },
      }
    : state;
const confirmed = (state: OperationState, value: boolean): OperationState =>
  state.operation.kind === "REVIEWING" && !state.disclosureRefused
    ? {
        ...state,
        operation: {
          ...state.operation,
          confirmed: value,
        },
      }
    : state;
const reset = (
  state: OperationState,
  action: Extract<
    OperationAction,
    {
      type: "RESET";
    }
  >,
): OperationState =>
  operationBusy(state) || (uncertain(state.operation) && !action.abandon)
    ? state
    : {
        ...action.state,
        dateEntries: {},
        disclosureRevision: state.disclosureRevision + 1,
        disclosureRefused: state.disclosureRefused || action.state.disclosureRefused,
        context: state.context,
        disclosureContext: state.disclosureContext,
      };
const checkingStatus = (state: OperationState, identity: EffectIdentity): OperationState =>
  state.operation.kind === "UNRESOLVED" && !operationBusy(state) && current(state, identity)
    ? append(
        {
          ...state,
          operation: {
            ...state.operation,
            effect: identity,
          },
        },
        {
          kind: "CHECK_OPERATION",
          identity,
          source: state.operation.source,
        },
      )
    : state;
const notAccepted = (
  state: OperationState,
  action: Extract<
    OperationAction,
    {
      type: "NOT_ACCEPTED";
    }
  >,
): OperationState =>
  completing(state, action.identity)
    ? {
        ...state,
        operation: {
          kind: "EDITING",
          exposed: null,
        },

        notice: {
          kind: "local",
          reason: "notAccepted",
        },
      }
    : state;
const stillUnresolved = (
  state: OperationState,
  identity: EffectIdentity,
  finalAt: string,
): OperationState =>
  completing(state, identity) && state.operation.kind === "UNRESOLVED"
    ? {
        ...state,
        operation: { ...state.operation, effect: null },
        notice: { kind: "pending", finalAt },
      }
    : state;
const readCurrent = (
  state: OperationState,
  action: Extract<OperationAction, { type: "READ_CURRENT" }>,
): OperationState =>
  current(state, action.identity) && !operationBusy(state) && state.disclosureRead === null
    ? append(
        { ...state, disclosureRead: action.identity, notice: null },
        {
          kind: "READ_CURRENT",
          identity: action.identity,
          caseId: action.caseId,
          reference: action.reference,
        },
      )
    : state;
const disclosed = (
  state: OperationState,
  action: Extract<OperationAction, { type: "DISCLOSED" | "DISCLOSURE_READ_FAILED" }>,
): OperationState => {
  if (!current(state, action.identity) || state.disclosureRead?.id !== action.identity.id) {
    return state;
  }
  return action.type === "DISCLOSED"
    ? {
        ...state,
        caseObservation: action.value,
        disclosureRead: null,
        disclosureContext: state.context,
        disclosureRefused: false,
        notice: null,
      }
    : {
        ...state,
        disclosureRead: null,
        notice: action.notice,
        disclosureRefused: state.disclosureRefused || action.refused,
        disclosureRevision: state.disclosureRevision + 1,
      };
};
type NavigationAction = Exclude<
  OperationAction,
  {
    type:
      | "EDIT"
      | "DATE_ENTRY"
      | "CLEAR_DATE_ENTRIES"
      | "EDIT_FIELDS"
      | "READ_CURRENT"
      | "RESTORE_CREATION_DRAFT"
      | "DISCLOSED"
      | "DISCLOSURE_READ_FAILED"
      | "READ_CONFLICT"
      | "CONFLICT_OBSERVED"
      | "CONFLICT_READ_FAILED"
      | "REVIEW_LATEST"
      | "PREVIEW"
      | "EXECUTE"
      | "REFUSE"
      | "RECORD"
      | "REVIEW"
      | "HANDOFF"
      | "OPERATION_PENDING";
  }
>;
const navigation = (state: OperationState, action: NavigationAction): OperationState => {
  switch (action.type) {
    case "CONTEXT":
      return contextChanged(state, action);
    case "INVALIDATE":
      return invalidate(state, action.refused);
    case "RESTORE":
      return {
        ...state,
        disclosureRefused: false,
      };
    case "RETURN":
      return returned(state);
    case "CONFIRM":
      return confirmed(state, action.value);
    case "DISCARD":
      return operationBusy(state)
        ? state
        : {
            ...state,
            pendingDiscard: action.kind,
          };
    case "RESET":
      return reset(state, action);
    case "CHECK_OPERATION":
      return checkingStatus(state, action.identity);
    case "NOT_ACCEPTED":
      return notAccepted(state, action);
    case "EFFECT_STARTED":
      return {
        ...state,
        effects: state.effects.filter((effect) => effect.identity.id !== action.id),
      };
  }
};
const handoff = (state: OperationState, identity: EffectIdentity): OperationState =>
  completing(state, identity) && state.operation.kind === "PREPARING"
    ? { ...state, operation: { ...state.operation, kind: "SUBMITTING" } }
    : state;
export const canRestoreCreationDraft = (state: OperationState): boolean => {
  const previous = state.disclosureContext;
  const next = state.context;
  const masked = previous !== next || state.disclosureRefused;
  return (
    masked &&
    previous.target === next.target &&
    state.target === next.target &&
    state.entry.subject === "CASE" &&
    state.entry.kind === "OPEN" &&
    state.caseObservation === null &&
    state.operation.kind === "EDITING"
  );
};
// Reject a scheduled restore if a refusal or another phase transition intervened.
const restoreCreationDraft = (
  state: OperationState,
  action: Extract<OperationAction, { type: "RESTORE_CREATION_DRAFT" }>,
): OperationState =>
  current(state, action.identity) &&
  state.operation === action.editing &&
  state.disclosureRevision === action.disclosureRevision &&
  canRestoreCreationDraft(state)
    ? { ...state, disclosureContext: state.context, disclosureRefused: false }
    : state;
type DisclosureAction = Extract<
  OperationAction,
  { type: "READ_CURRENT" | "RESTORE_CREATION_DRAFT" | "DISCLOSED" | "DISCLOSURE_READ_FAILED" }
>;
const isDisclosureAction = (action: OperationAction): action is DisclosureAction =>
  action.type === "READ_CURRENT" ||
  action.type === "RESTORE_CREATION_DRAFT" ||
  action.type === "DISCLOSED" ||
  action.type === "DISCLOSURE_READ_FAILED";
const disclosureTransition = (state: OperationState, action: DisclosureAction): OperationState => {
  if (action.type === "RESTORE_CREATION_DRAFT") {
    return restoreCreationDraft(state, action);
  }
  return action.type === "READ_CURRENT" ? readCurrent(state, action) : disclosed(state, action);
};
type ConflictAction = Extract<
  OperationAction,
  { type: "READ_CONFLICT" | "CONFLICT_OBSERVED" | "CONFLICT_READ_FAILED" | "REVIEW_LATEST" }
>;
const isConflictAction = (action: OperationAction): action is ConflictAction =>
  ["READ_CONFLICT", "CONFLICT_OBSERVED", "CONFLICT_READ_FAILED", "REVIEW_LATEST"].includes(
    action.type,
  );
const conflictTransition = (state: OperationState, action: ConflictAction): OperationState => {
  switch (action.type) {
    case "READ_CONFLICT":
      return readConflict(state, action);
    case "CONFLICT_OBSERVED":
    case "CONFLICT_READ_FAILED":
      return conflictObservation(state, action);
    case "REVIEW_LATEST":
      return reviewLatest(state, action);
  }
};
const dateEntry = (
  state: OperationState,
  action: Extract<OperationAction, { type: "DATE_ENTRY" }>,
): OperationState => {
  const value = state.values[action.path];
  const canonical = typeof value === "string" ? value : "";
  if (
    !canEdit(state) ||
    state.disclosureRefused ||
    state.disclosureContext !== state.context ||
    canonical !== action.canonical
  ) {
    return state;
  }
  if (action.parts === null || action.parts.length === 0) {
    const entries = withoutDateEntries(state.dateEntries, [action.path]);
    return entries === state.dateEntries ? state : { ...state, dateEntries: entries };
  }
  const next =
    canonical === "" ? state : edit(state, { type: "EDIT", path: action.path, value: "" });
  const draft = { canonical: "", parts: action.parts };
  if (sameDateEntryDraft(next.dateEntries[action.path], draft)) {
    return next;
  }
  return { ...next, dateEntries: { ...next.dateEntries, [action.path]: draft } };
};
type FormEntryAction = Extract<
  OperationAction,
  { type: "EDIT" | "EDIT_FIELDS" | "DATE_ENTRY" | "CLEAR_DATE_ENTRIES" }
>;
const isFormEntryAction = (action: OperationAction): action is FormEntryAction =>
  ["EDIT", "EDIT_FIELDS", "DATE_ENTRY", "CLEAR_DATE_ENTRIES"].includes(action.type);
const formEntryTransition = (state: OperationState, action: FormEntryAction): OperationState => {
  switch (action.type) {
    case "EDIT":
      return edit(state, action);
    case "EDIT_FIELDS":
      return editFields(state, action);
    case "DATE_ENTRY":
      return dateEntry(state, action);
    case "CLEAR_DATE_ENTRIES":
      return Object.keys(state.dateEntries).length === 0 ? state : { ...state, dateEntries: {} };
  }
};
/** State changes issue effects; completions require the matching epoch, target and effect ID. */
export const operationReducer = (
  state: OperationState,
  action: OperationAction,
): OperationState => {
  if (isFormEntryAction(action)) {
    return formEntryTransition(state, action);
  }
  if (isConflictAction(action)) {
    return conflictTransition(state, action);
  }
  if (isDisclosureAction(action)) {
    return disclosureTransition(state, action);
  }
  switch (action.type) {
    case "PREVIEW":
      return preview(state, action);
    case "EXECUTE":
      return execute(state, action.identity);
    case "HANDOFF":
      return handoff(state, action.identity);
    case "REFUSE":
      return refuse(state, action);
    case "RECORD":
      return record(state, action);
    case "OPERATION_PENDING":
      return stillUnresolved(state, action.identity, action.finalAt);
    case "REVIEW":
      return reviewed(state, action);
    default:
      return navigation(state, action);
  }
};
export const operationView = (state: OperationState) => {
  const phase = state.operation;
  const source = operationSource(phase);
  return {
    ...state,
    phase: phase.kind,
    busy: operationBusy(state),
    priorUncertain: uncertain(phase),
    prepared: source?.prepared ?? null,
    review:
      phase.kind === "REVIEWING" || phase.kind === "PREPARING" || phase.kind === "SUBMITTING"
        ? phase.review
        : null,
    receipt: phase.kind === "ACCEPTED" ? phase.receipt : null,
    confirmed:
      phase.kind === "REVIEWING"
        ? phase.confirmed
        : (phase.kind === "PREPARING" || phase.kind === "SUBMITTING") && phase.review !== null,
  };
};
