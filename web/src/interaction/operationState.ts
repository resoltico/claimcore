import type { OperationReceipt, OperationReview, RecoveryItem } from "@contracts/client";
import type { Notice } from "../api/notices";
import type { FormValues } from "../protocol/forms";
import type { PreparedRequest } from "../protocol/preparedRequest";
import { refusalPath } from "./operationForm";

export type EffectIdentity = { id: string; token: string; target: string };
export type PendingSource =
  | { kind: "LOCAL"; prepared: PreparedRequest<"operation.execute">; recoveryId: string | null }
  | { kind: "RECOVERED"; item: RecoveryItem };
type LocalSource = Extract<PendingSource, { kind: "LOCAL" }>;
export type OperationPhase =
  | { tag: "EDITING"; exposed: LocalSource | null }
  | { tag: "PREVIEWING"; source: LocalSource; effect: EffectIdentity }
  | { tag: "REVIEWING"; source: LocalSource; review: OperationReview; confirmed: boolean }
  | {
      tag: "SUBMITTING";
      source: PendingSource;
      effect: EffectIdentity;
      knowledge: "FRESH" | "UNCERTAIN";
      review: OperationReview | null;
    }
  | { tag: "UNCERTAIN"; source: PendingSource }
  | { tag: "ACCEPTED"; source: PendingSource; receipt: OperationReceipt | null };
export type OperationEffect =
  | { kind: "PREVIEW"; identity: EffectIdentity; source: LocalSource }
  | { kind: "EXECUTE"; identity: EffectIdentity; source: PendingSource }
  | { kind: "FORGET"; identity: EffectIdentity; recoveryId: string }
  | { kind: "RECORDED"; identity: EffectIdentity; receipt: OperationReceipt }
  | { kind: "DISCLOSURE_REFUSED"; identity: EffectIdentity; notice: Notice };
export type OperationState = {
  target: string;
  context: { token: string; target: string };
  disclosureToken: string;
  disclosureRefused: boolean;
  values: FormValues;
  baseline: FormValues;
  generation: number;
  operation: OperationPhase;
  effects: ReadonlyArray<OperationEffect>;
  notice: Notice | null;
  fieldError: string | null;
  pendingDiscard: string | null;
};
export type OperationAction =
  | { type: "RESET"; state: OperationState; abandon: boolean }
  | { type: "EDIT"; path: string; value: string | boolean; id: string }
  | { type: "DISCARD"; kind: string | null }
  | {
      type: "PREVIEW";
      prepared: PreparedRequest<"operation.execute">;
      identity: EffectIdentity;
      generation: number;
    }
  | { type: "REVIEW"; review: OperationReview; identity: EffectIdentity }
  | {
      type: "RECORD";
      receipt: OperationReceipt;
      identity: EffectIdentity;
      recoveryId: string | null;
    }
  | {
      type: "REFUSE";
      notice: Notice;
      target: string | null;
      uncertain: boolean;
      disclosure: boolean;
      identity?: EffectIdentity;
      recoveryId?: string | null;
    }
  | { type: "CONTEXT"; token: string; target: string }
  | { type: "INVALIDATE"; refused: boolean }
  | { type: "RESTORE" }
  | { type: "RETURN" }
  | { type: "CONFIRM"; value: boolean }
  | { type: "EXECUTE"; identity: EffectIdentity }
  | { type: "RECOVER"; item: RecoveryItem }
  | { type: "ACKNOWLEDGE"; identity: EffectIdentity }
  | { type: "EFFECT_STARTED"; id: string };
export const operationSource = (phase: OperationPhase): PendingSource | null =>
  phase.tag === "EDITING" ? phase.exposed : phase.source;
const operationBusy = (state: OperationState): boolean =>
  state.operation.tag === "PREVIEWING" || state.operation.tag === "SUBMITTING";
const canEdit = (state: OperationState): boolean =>
  state.operation.tag === "EDITING" || state.operation.tag === "REVIEWING";
const current = (state: OperationState, identity: EffectIdentity): boolean =>
  state.context.token === identity.token && state.context.target === identity.target;
const completing = (state: OperationState, identity: EffectIdentity): boolean =>
  current(state, identity) &&
  operationBusy(state) &&
  (state.operation.tag === "PREVIEWING" || state.operation.tag === "SUBMITTING") &&
  state.operation.effect.id === identity.id;
const append = (state: OperationState, effect: OperationEffect): OperationState => ({
  ...state,
  effects: [...state.effects, effect],
});
const sourceWithRecovery = (
  source: PendingSource,
  recoveryId: string | null | undefined,
): PendingSource =>
  source.kind === "LOCAL" ? { ...source, recoveryId: recoveryId ?? source.recoveryId } : source;
const localSource = (phase: OperationPhase): LocalSource | null => {
  const source = operationSource(phase);
  return source?.kind === "LOCAL" ? source : null;
};
const uncertain = (phase: OperationPhase): boolean =>
  phase.tag === "UNCERTAIN" || (phase.tag === "SUBMITTING" && phase.knowledge === "UNCERTAIN");
const edit = (
  state: OperationState,
  action: Extract<OperationAction, { type: "EDIT" }>,
): OperationState => {
  if (!canEdit(state) || state.values[action.path] === action.value) {
    return state;
  }
  const source = operationSource(state.operation);
  return {
    ...state,
    generation: state.generation + 1,
    operation: { tag: "EDITING", exposed: null },
    values: {
      ...state.values,
      [action.path]: action.value,
      operationId: source === null ? state.values["operationId"]! : action.id,
    },
    notice: null,
    fieldError: null,
  };
};
const invalidate = (state: OperationState, refused: boolean): OperationState => {
  const phase = state.operation;
  let operation: OperationPhase;
  if (phase.tag === "SUBMITTING" || phase.tag === "UNCERTAIN") {
    operation = { tag: "UNCERTAIN", source: phase.source };
  } else if (phase.tag === "ACCEPTED") {
    operation = { ...phase, receipt: null };
  } else {
    operation = { tag: "EDITING", exposed: localSource(phase) };
  }
  return { ...state, operation, disclosureRefused: refused, effects: [] };
};
const preview = (
  state: OperationState,
  action: Extract<OperationAction, { type: "PREVIEW" }>,
): OperationState => {
  if (
    !canEdit(state) ||
    !current(state, action.identity) ||
    state.generation !== action.generation
  ) {
    return state;
  }
  const source: LocalSource = { kind: "LOCAL", prepared: action.prepared, recoveryId: null };
  return append(
    {
      ...state,
      operation: { tag: "PREVIEWING", source, effect: action.identity },
      notice: null,
      fieldError: null,
    },
    { kind: "PREVIEW", identity: action.identity, source },
  );
};
const execute = (state: OperationState, identity: EffectIdentity): OperationState => {
  const phase = state.operation;
  if (!current(state, identity)) {
    return state;
  }
  const reviewed =
    phase.tag === "REVIEWING" &&
    phase.confirmed &&
    !state.disclosureRefused &&
    state.disclosureToken === identity.token;
  if (phase.tag !== "UNCERTAIN" && !reviewed) {
    return state;
  }
  const source = operationSource(phase);
  if (source === null || (source.kind === "RECOVERED" && !source.item.offers.includes("RETRY"))) {
    return state;
  }
  return append(
    {
      ...state,
      operation: {
        tag: "SUBMITTING",
        source,
        effect: identity,
        knowledge: phase.tag === "UNCERTAIN" ? "UNCERTAIN" : "FRESH",
        review: phase.tag === "REVIEWING" ? phase.review : null,
      },
      notice: null,
    },
    { kind: "EXECUTE", identity, source },
  );
};
const refuse = (
  state: OperationState,
  action: Extract<OperationAction, { type: "REFUSE" }>,
): OperationState => {
  if (action.identity !== undefined && !completing(state, action.identity)) {
    return state;
  }
  const source = operationSource(state.operation);
  const sticky = uncertain(state.operation) || action.uncertain;
  const retained = source === null ? null : sourceWithRecovery(source, action.recoveryId);
  const operation: OperationPhase =
    sticky && retained !== null
      ? { tag: "UNCERTAIN", source: retained }
      : { tag: "EDITING", exposed: retained?.kind === "LOCAL" ? retained : null };
  const next = {
    ...state,
    operation,
    notice: action.notice,
    fieldError: refusalPath(action.target, state.values),
    disclosureRefused: action.disclosure,
  };
  return action.disclosure && action.identity !== undefined
    ? append(next, { kind: "DISCLOSURE_REFUSED", identity: action.identity, notice: action.notice })
    : next;
};
const record = (
  state: OperationState,
  action: Extract<OperationAction, { type: "RECORD" }>,
): OperationState => {
  if (!completing(state, action.identity)) {
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
        tag: "ACCEPTED",
        source: sourceWithRecovery(source, action.recoveryId),
        receipt: action.receipt,
      },
      pendingDiscard: null,
      notice: null,
      disclosureToken: action.identity.token,
      disclosureRefused: false,
    },
    { kind: "RECORDED", identity: action.identity, receipt: action.receipt },
  );
};
const acknowledge = (state: OperationState, identity: EffectIdentity): OperationState => {
  if (!current(state, identity) || uncertain(state.operation) || operationBusy(state)) {
    return state;
  }
  const source = operationSource(state.operation);
  const recoveryId = source?.kind === "LOCAL" ? source.recoveryId : source?.item.recoveryId;
  return recoveryId === null || recoveryId === undefined
    ? state
    : append(state, { kind: "FORGET", identity, recoveryId });
};
const reviewed = (
  state: OperationState,
  action: Extract<OperationAction, { type: "REVIEW" }>,
): OperationState => {
  if (!completing(state, action.identity) || state.operation.tag !== "PREVIEWING") {
    return state;
  }
  return {
    ...state,
    operation: {
      tag: "REVIEWING",
      source: state.operation.source,
      review: action.review,
      confirmed: false,
    },
    disclosureToken: action.identity.token,
    disclosureRefused: false,
  };
};
const contextChanged = (
  state: OperationState,
  action: Extract<OperationAction, { type: "CONTEXT" }>,
): OperationState =>
  state.context.token === action.token && state.context.target === action.target
    ? state
    : { ...invalidate(state, false), context: { token: action.token, target: action.target } };
const returned = (state: OperationState): OperationState =>
  canEdit(state)
    ? { ...state, operation: { tag: "EDITING", exposed: localSource(state.operation) } }
    : state;
const confirmed = (state: OperationState, value: boolean): OperationState =>
  state.operation.tag === "REVIEWING" && !state.disclosureRefused
    ? { ...state, operation: { ...state.operation, confirmed: value } }
    : state;
const reset = (
  state: OperationState,
  action: Extract<OperationAction, { type: "RESET" }>,
): OperationState =>
  operationBusy(state) || (uncertain(state.operation) && !action.abandon)
    ? state
    : { ...action.state, disclosureToken: state.disclosureToken };
export const recoveryTarget = (item: RecoveryItem): string =>
  item.scope === "CASE"
    ? `CASE:${item.caseReference?.toUpperCase() ?? ""}`
    : `AUTHORITY:${item.targetActorId ?? ""}`;
const recovered = (state: OperationState, item: RecoveryItem): OperationState => {
  if (state.operation.tag !== "EDITING" || state.operation.exposed !== null) {
    return state;
  }
  const target = recoveryTarget(item);
  return {
    ...state,
    target,
    context: { ...state.context, target },
    operation: { tag: "UNCERTAIN", source: { kind: "RECOVERED", item } },
    notice: null,
  };
};
type NavigationAction = Exclude<
  OperationAction,
  { type: "EDIT" | "PREVIEW" | "EXECUTE" | "REFUSE" | "RECORD" | "REVIEW" }
>;
const navigation = (state: OperationState, action: NavigationAction): OperationState => {
  switch (action.type) {
    case "CONTEXT":
      return contextChanged(state, action);
    case "INVALIDATE":
      return invalidate(state, action.refused);
    case "RESTORE":
      return { ...state, disclosureRefused: false };
    case "RETURN":
      return returned(state);
    case "CONFIRM":
      return confirmed(state, action.value);
    case "DISCARD":
      return operationBusy(state) ? state : { ...state, pendingDiscard: action.kind };
    case "RESET":
      return reset(state, action);
    case "RECOVER":
      return recovered(state, action.item);
    case "ACKNOWLEDGE":
      return acknowledge(state, action.identity);
    case "EFFECT_STARTED":
      return {
        ...state,
        effects: state.effects.filter((effect) => effect.identity.id !== action.id),
      };
  }
};
/** Effects are issued only by explicit events; completions are fenced by epoch, target and effect ID. */
export const operationReducer = (
  state: OperationState,
  action: OperationAction,
): OperationState => {
  switch (action.type) {
    case "EDIT":
      return edit(state, action);
    case "PREVIEW":
      return preview(state, action);
    case "EXECUTE":
      return execute(state, action.identity);
    case "REFUSE":
      return refuse(state, action);
    case "RECORD":
      return record(state, action);
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
    phase: phase.tag,
    busy: operationBusy(state),
    priorUncertain: uncertain(phase),
    prepared: source?.kind === "LOCAL" ? source.prepared : null,
    recovery: source?.kind === "RECOVERED" ? source.item : null,
    review: phase.tag === "REVIEWING" || phase.tag === "SUBMITTING" ? phase.review : null,
    receipt: phase.tag === "ACCEPTED" ? phase.receipt : null,
    confirmed:
      phase.tag === "REVIEWING"
        ? phase.confirmed
        : phase.tag === "SUBMITTING" && phase.review !== null,
  };
};
