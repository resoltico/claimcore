import { beginMutation, completeMutation } from "./mutationLifecycle";
import { projectOperation } from "./operationState";
import { prepareRequest } from "../protocol/preparedRequest";
import type { CommandKind, DraftValues } from "./metadata";
import { isGroupedDraftValues } from "./metadata";
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

  mutation: { phase: "EDITING", prepared: null },

  review: null,
  receipt: null,

  disclosureRefused: false,
  message: null,
  fieldError: null,
});
const edit = (state: OperationState, action: Extract<OperationAction, { type: "EDIT" }>) =>
  !editable(state) ||
  isGroupedDraftValues(state.values) ||
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
  if (!editable(state) || !isGroupedDraftValues(state.values)) {
    return state;
  }
  const group = state.values[action.group];
  if (
    group === undefined ||
    group.mode !== "REPLACE" ||
    group.values[action.field] === action.value
  ) {
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
  if (!editable(state) || !isGroupedDraftValues(state.values)) {
    return state;
  }
  const group = state.values[action.group];
  if (group === undefined || group.mode === action.mode) {
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
    mutation: { phase: "PREVIEWING", prepared, requestId: action.requestId },

    message: null,
    fieldError: null,
  };
};
const complete = (state: OperationState, action: CompletionResponse): OperationState => {
  if (state.pending?.requestId !== action.requestId) {
    return state;
  }
  let attempt: Parameters<typeof completeMutation>[2] = "NOT_ACCEPTED";
  let evidence: Parameters<typeof completeMutation>[3] = "NONE";
  if (action.type === "ACCEPTED") {
    attempt = "ACCEPTED";
    evidence = "CONTENT_ACCEPTED";
  } else if (action.type === "OUTCOME_UNKNOWN") {
    attempt = "UNCONFIRMED";
  } else {
    evidence = action.evidence ?? "NONE";
  }
  const mutation = completeMutation(state.mutation, action.requestId, attempt, evidence);
  if (action.type === "ACCEPTED") {
    return {
      ...state,
      mutation,

      receipt: action.receipt,
      disclosureRefused: false,

      message: null,
    };
  }
  if (action.type === "OUTCOME_UNKNOWN") {
    return state.pending.kind === "SUBMIT"
      ? {
          ...state,
          mutation,

          message: action.message,
        }
      : state;
  }
  return {
    ...state,
    mutation,

    disclosureRefused: action.disclosureRefused === true,
    review: null,

    message: action.message,
    fieldError: action.field === null ? null : { name: action.field, message: action.message },
  };
};
const submit = (state: OperationState, requestId: number): OperationState =>
  (state.delivery === "REVIEWING" || state.delivery === "OUTCOME_UNKNOWN") &&
  state.requestBytes !== null
    ? {
        ...state,
        mutation: beginMutation(state.mutation, requestId),
        message: null,
      }
    : state;
const reviewed = (
  state: OperationState,
  action: Extract<OperationAction, { type: "REVIEWED" }>,
): OperationState => {
  if (
    state.pending?.kind !== "PREVIEW" ||
    state.pending.requestId !== action.requestId ||
    state.prepared === null
  ) {
    return state;
  }
  return {
    ...state,
    mutation: { phase: "REVIEWING", prepared: state.prepared },
    review: action.review,
  };
};
const transition = (
  state: OperationState,
  action: Exclude<OperationAction, EditingAction>,
): OperationState => {
  switch (action.type) {
    case "INPUT_INVALID":
      return editable(state)
        ? {
            ...state,
            mutation: { phase: "DEFINITELY_REJECTED", prepared: state.prepared },

            message: action.message,
            fieldError: { name: action.field, message: action.message },
          }
        : state;
    case "PREVIEWING":
      return beginPreview(state, action);
    case "REVIEWED":
      return reviewed(state, action);
    case "SUBMITTING":
      return submit(state, action.requestId);
    case "RETURN_TO_EDIT":
      return state.delivery === "REVIEWING"
        ? {
            ...state,
            mutation: { phase: "EDITING", prepared: state.prepared },
            review: null,
          }
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
export const operationReducer = (
  state: OperationState,
  action: OperationAction,
): OperationState => {
  const result =
    action.type === "EDIT" ||
    action.type === "EDIT_CORRECTION" ||
    action.type === "SET_CORRECTION_MODE" ||
    action.type === "EDIT_REFERENCE" ||
    action.type === "CHANGE_COMMAND"
      ? editingReducer(state, action)
      : transition(state, action);
  return result === state ? state : projectOperation(result);
};
