import { roleAdmitted } from "./roleAvailability";
import { operationEntry } from "../protocol/operationCatalog";
import { inputErrors } from "./inputErrors";
import {
  operationSource,
  type EffectIdentity,
  type OperationPhase,
  type OperationEffect,
  type OperationState,
  type OperationAction,
  type LocalSource,
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
    (action.path !== "target.kind" && action.path !== "act.scope.kind")
  ) {
    return false;
  }
  const values = { ...state.values, [action.path]: action.value };
  const role = values["act.role"];
  const principal = values["target.kind"];
  const scope = values["act.scope.kind"];
  return (
    typeof role === "string" &&
    typeof principal === "string" &&
    !roleAdmitted(role, principal, typeof scope === "string" ? scope : undefined)
  );
};
const edit = (state: OperationState, action: EditAction): OperationState => {
  if (!canEdit(state) || state.values[action.path] === action.value) {
    return state;
  }
  const source = operationSource(state.operation);
  return {
    ...state,
    generation: state.generation + 1,
    operation: {
      kind: "EDITING",
      exposed: null,
    },
    values: {
      ...state.values,
      [action.path]: action.value,
      ...(clearsRoleSelection(state, action) ? { "act.role": "" } : {}),
      operationId: source === null ? state.values["operationId"]! : action.id,
    },
    notice: null,
    fieldErrors: {},
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
  if (!changed) {
    return state;
  }
  const source = operationSource(state.operation);
  return {
    ...state,
    generation: state.generation + 1,
    operation: { kind: "EDITING", exposed: null },
    values: { ...values, operationId: source === null ? state.values["operationId"]! : action.id },
    notice: null,
    fieldErrors: {},
  };
};
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
    disclosureRefused: refused,
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
    !current(state, action.identity) ||
    state.generation !== action.generation
  ) {
    return state;
  }
  const source: LocalSource = {
    prepared: action.prepared,
  };
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
      action.notice,
      action.target,
      operationEntry(state.entry),
      state.values,
    ),
    disclosureRefused: action.disclosure,
  };
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
          kind: "CHECK_STATUS",
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
        values: {
          ...state.values,
          operationId: action.id,
        },
        notice: {
          kind: "local",
          reason: "notAccepted",
        },
      }
    : state;
const stillUnresolved = (state: OperationState, identity: EffectIdentity): OperationState =>
  completing(state, identity) && state.operation.kind === "UNRESOLVED"
    ? {
        ...state,
        operation: { ...state.operation, effect: null },
        notice: { kind: "local", reason: "unresolved" },
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
    : { ...state, disclosureRead: null, notice: action.notice, disclosureRefused: action.refused };
};
type NavigationAction = Exclude<
  OperationAction,
  {
    type:
      | "EDIT"
      | "EDIT_FIELDS"
      | "READ_CURRENT"
      | "DISCLOSED"
      | "DISCLOSURE_READ_FAILED"
      | "PREVIEW"
      | "EXECUTE"
      | "REFUSE"
      | "RECORD"
      | "REVIEW"
      | "HANDOFF"
      | "STATUS_UNRESOLVED";
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
    case "CHECK_STATUS":
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
type DisclosureAction = Extract<
  OperationAction,
  { type: "READ_CURRENT" | "DISCLOSED" | "DISCLOSURE_READ_FAILED" }
>;
const isDisclosureAction = (action: OperationAction): action is DisclosureAction =>
  action.type === "READ_CURRENT" ||
  action.type === "DISCLOSED" ||
  action.type === "DISCLOSURE_READ_FAILED";
const disclosureTransition = (state: OperationState, action: DisclosureAction): OperationState =>
  action.type === "READ_CURRENT" ? readCurrent(state, action) : disclosed(state, action);
/** Effects are issued only by explicit events; completions are fenced by epoch, target and effect ID. */
export const operationReducer = (
  state: OperationState,
  action: OperationAction,
): OperationState => {
  if (isDisclosureAction(action)) {
    return disclosureTransition(state, action);
  }
  switch (action.type) {
    case "EDIT":
      return edit(state, action);
    case "EDIT_FIELDS":
      return editFields(state, action);
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
    case "STATUS_UNRESOLVED":
      return stillUnresolved(state, action.identity);
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
