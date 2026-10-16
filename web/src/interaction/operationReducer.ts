import { accumulateKnowledge } from "../protocol/delivery";
import { prepareRequest } from "../protocol/preparedRequest";
import type { CommandKind, DraftValues } from "./metadata";
import { isCorrectionValues } from "./metadata";
import { freezeRequest } from "./operationRequest";
import type { CompletionResponse, EditingAction } from "./operationReducerTypes";
import type { OperationAction, OperationState } from "./operationState";

export { initialOperation } from "./operationInitial";

export type { OperationAction, OperationState } from "./operationState";

export const editable = (state: OperationState): boolean =>
  state.delivery === "EDITING" || state.delivery === "DEFINITELY_REJECTED";
const reset = (
  state: OperationState,
  values: DraftValues,
  operationId: string,
  command: CommandKind = state.command,
): OperationState => ({
  ...state,
  operationId,
  command,
  values,
  requestSha256: null,
  requestBytes: null,
  prepared: null,
  review: null,
  receipt: null,
  delivery: "EDITING",
  unresolved: false,
  disclosureRefused: false,
  message: null,
  fieldError: null,
  exposedRequest: null,
  pending: null,
});
const edit = (state: OperationState, action: Extract<OperationAction, { type: "EDIT" }>) =>
  !editable(state) ||
  isCorrectionValues(state.values) ||
  state.values[action.field] === action.value
    ? state
    : reset(
        state,
        { ...state.values, [action.field]: action.value },
        state.exposedRequest === null ? state.operationId : action.nextOperationId,
      );
const editCorrection = (
  state: OperationState,
  action: Extract<OperationAction, { type: "EDIT_CORRECTION" }>,
) => {
  if (!editable(state) || !isCorrectionValues(state.values)) {
    return state;
  }
  const group = state.values[action.group];
  if (group.mode !== "REPLACE" || group.values[action.field] === action.value) {
    return state;
  }
  return reset(
    state,
    {
      ...state.values,
      [action.group]: { ...group, values: { ...group.values, [action.field]: action.value } },
    },
    state.exposedRequest === null ? state.operationId : action.nextOperationId,
  );
};
const setCorrectionMode = (
  state: OperationState,
  action: Extract<OperationAction, { type: "SET_CORRECTION_MODE" }>,
) => {
  if (!editable(state) || !isCorrectionValues(state.values)) {
    return state;
  }
  const group = state.values[action.group];
  if (group.mode === action.mode) {
    return state;
  }
  return reset(
    state,
    { ...state.values, [action.group]: { ...group, mode: action.mode } },
    state.exposedRequest === null ? state.operationId : action.nextOperationId,
  );
};
const editReference = (
  state: OperationState,
  action: Extract<OperationAction, { type: "EDIT_REFERENCE" }>,
): OperationState =>
  !editable(state) || state.caseReference === action.value
    ? state
    : {
        ...reset(
          state,
          state.values,
          state.exposedRequest === null ? state.operationId : action.nextOperationId,
        ),
        caseReference: action.value,
      };
const change = (
  state: OperationState,
  action: Extract<OperationAction, { type: "CHANGE_COMMAND" }>,
) =>
  editable(state) ? reset(state, action.values, action.nextOperationId, action.command) : state;
const beginPreview = (
  state: OperationState,
  action: Extract<OperationAction, { type: "PREVIEWING" }>,
): OperationState => {
  if (!editable(state) || action.draft.operationId !== state.operationId) {
    return state;
  }
  if (state.prepared !== null && state.prepared !== action.prepared) {
    return state;
  }
  const draft = state.exposedRequest ?? freezeRequest(action.draft);
  const prepared = action.prepared ?? prepareRequest("case.execute", draft);
  return {
    ...state,
    delivery: "PREVIEWING",
    exposedRequest: draft,
    requestBytes: prepared.bytes,
    prepared,
    message: null,
    fieldError: null,
    pending: { kind: "PREVIEW", requestId: action.requestId },
  };
};
const completionKnowledge = (state: OperationState, action: CompletionResponse): string => {
  let attempt = "NOT_ACCEPTED";
  let evidence = "NONE";
  if (action.type === "ACCEPTED") {
    attempt = "ACCEPTED";
    evidence = "CONTENT_ACCEPTED";
  } else if (action.type === "OUTCOME_UNKNOWN") {
    attempt = "UNCONFIRMED";
  } else {
    evidence = action.evidence ?? "NONE";
  }
  return accumulateKnowledge(state.unresolved ? "UNCONFIRMED" : "NOT_SENT", attempt, evidence);
};
const complete = (state: OperationState, action: CompletionResponse): OperationState => {
  if (state.pending?.requestId !== action.requestId) {
    return state;
  }
  const unresolved = completionKnowledge(state, action) === "UNCONFIRMED";
  if (action.type === "ACCEPTED") {
    return {
      ...state,
      delivery: "ACCEPTED",
      unresolved,
      receipt: action.receipt,
      pending: null,
      message: null,
    };
  }
  if (action.type === "OUTCOME_UNKNOWN") {
    return state.pending.kind === "SUBMIT"
      ? {
          ...state,
          delivery: "OUTCOME_UNKNOWN",
          unresolved,
          pending: null,
          message: action.message,
        }
      : state;
  }
  return {
    ...state,
    delivery: unresolved ? "OUTCOME_UNKNOWN" : "DEFINITELY_REJECTED",
    unresolved,
    disclosureRefused: action.disclosureRefused === true,
    review: null,
    requestSha256: null,
    pending: null,
    message: action.message,
    fieldError: action.field === null ? null : { name: action.field, message: action.message },
  };
};
const submit = (state: OperationState, requestId: number): OperationState =>
  (state.delivery === "REVIEWING" || state.delivery === "OUTCOME_UNKNOWN") &&
  state.requestBytes !== null
    ? { ...state, delivery: "SUBMITTING", pending: { kind: "SUBMIT", requestId }, message: null }
    : state;
const transition = (
  state: OperationState,
  action: Exclude<OperationAction, EditingAction>,
): OperationState => {
  switch (action.type) {
    case "INPUT_INVALID":
      return editable(state)
        ? {
            ...state,
            delivery: "DEFINITELY_REJECTED",
            message: action.message,
            fieldError: { name: action.field, message: action.message },
          }
        : state;
    case "PREVIEWING":
      return beginPreview(state, action);
    case "REVIEWED":
      return state.pending?.kind === "PREVIEW" && state.pending.requestId === action.requestId
        ? {
            ...state,
            delivery: "REVIEWING",
            review: action.review,
            requestSha256: action.requestSha256,
            pending: null,
          }
        : state;
    case "SUBMITTING":
      return submit(state, action.requestId);
    case "RETURN_TO_EDIT":
      return state.delivery === "REVIEWING"
        ? { ...state, delivery: "EDITING", review: null, requestSha256: null, pending: null }
        : state;
    case "RESET_MESSAGE":
      return { ...state, message: null };
    default:
      return complete(state, action);
  }
};
const editingReducer = (state: OperationState, action: EditingAction): OperationState => {
  switch (action.type) {
    case "EDIT":
      return edit(state, action);
    case "EDIT_CORRECTION":
      return editCorrection(state, action);
    case "SET_CORRECTION_MODE":
      return setCorrectionMode(state, action);
    case "EDIT_REFERENCE":
      return editReference(state, action);
    case "CHANGE_COMMAND":
      return change(state, action);
  }
};
export const operationReducer = (state: OperationState, action: OperationAction): OperationState =>
  action.type === "EDIT" ||
  action.type === "EDIT_CORRECTION" ||
  action.type === "SET_CORRECTION_MODE" ||
  action.type === "EDIT_REFERENCE" ||
  action.type === "CHANGE_COMMAND"
    ? editingReducer(state, action)
    : transition(state, action);
