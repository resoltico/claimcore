import type { AdvisoryReview, PreparationDetails, Receipt } from "../api/v2";
import type { CommandKind, DraftValues } from "./metadata";

export type DeliveryState =
  | "EDITING"
  | "PREPARING"
  | "PREPARATION_UNKNOWN"
  | "RETAINED_FOR_RECOVERY"
  | "REVIEWING"
  | "SUBMITTING"
  | "ACCEPTED"
  | "DEFINITELY_REJECTED"
  | "OUTCOME_UNKNOWN";
export type OperationState = {
  delivery: DeliveryState;
  operationId: string;
  caseReference: string;
  command: CommandKind;
  values: DraftValues;
  preparation: PreparationDetails | null;
  review: AdvisoryReview | null;
  receipt: Receipt | null;
  message: string | null;
  fieldError: { name: string; message: string } | null;
  requiresNewOperationId: boolean;
};
export type OperationAction =
  | { type: "EDIT"; field: string; value: string; nextOperationId: string }
  | { type: "EDIT_REFERENCE"; value: string; nextOperationId: string }
  | { type: "CHANGE_COMMAND"; command: CommandKind; values: DraftValues; nextOperationId: string }
  | { type: "PREPARING" }
  | { type: "PREPARED"; preparation: PreparationDetails; review: AdvisoryReview }
  | { type: "PREPARATION_UNKNOWN"; message: string }
  | { type: "RETAINED_FOR_RECOVERY"; preparation: PreparationDetails; message: string }
  | { type: "DEFINITELY_REJECTED"; message: string; field: string | null }
  | { type: "SUBMITTING" }
  | { type: "ACCEPTED"; receipt: Receipt }
  | { type: "OUTCOME_UNKNOWN"; message: string }
  | { type: "KEEP_FOR_RECOVERY" }
  | { type: "RESET_MESSAGE" };

const editable = (state: OperationState) =>
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
  preparation: null,
  review: null,
  receipt: null,
  delivery: "EDITING",
  message: null,
  fieldError: null,
  requiresNewOperationId: false,
});
const edit = (state: OperationState, action: Extract<OperationAction, { type: "EDIT" }>) =>
  !editable(state)
    ? state
    : reset(
        state,
        { ...state.values, [action.field]: action.value },
        state.requiresNewOperationId ? action.nextOperationId : state.operationId,
      );
const editReference = (
  state: OperationState,
  action: Extract<OperationAction, { type: "EDIT_REFERENCE" }>,
): OperationState =>
  !editable(state)
    ? state
    : {
        ...reset(
          state,
          state.values,
          state.requiresNewOperationId ? action.nextOperationId : state.operationId,
        ),
        caseReference: action.value,
      };
const change = (
  state: OperationState,
  action: Extract<OperationAction, { type: "CHANGE_COMMAND" }>,
) =>
  !editable(state) ? state : reset(state, action.values, action.nextOperationId, action.command);
const only = (
  state: OperationState,
  delivery: DeliveryState,
  message: string | null,
): OperationState => ({ ...state, delivery, message, fieldError: null });
const preparing = (state: OperationState) =>
  editable(state) || state.delivery === "PREPARATION_UNKNOWN"
    ? only(state, "PREPARING", null)
    : state;
const submitting = (state: OperationState) =>
  state.delivery === "REVIEWING" ? only(state, "SUBMITTING", null) : state;
const retain = (state: OperationState): OperationState =>
  state.delivery !== "REVIEWING"
    ? state
    : {
        ...state,
        delivery: "EDITING",
        preparation: null,
        review: null,
        requiresNewOperationId: true,
      };

const reject = (
  state: OperationState,
  action: Extract<OperationAction, { type: "DEFINITELY_REJECTED" }>,
): OperationState => ({
  ...only(state, "DEFINITELY_REJECTED", action.message),
  fieldError: action.field === null ? null : { name: action.field, message: action.message },
});

export const initialOperation = (
  operationId: string,
  command: CommandKind,
  values: DraftValues,
  caseReference: string,
): OperationState => ({
  delivery: "EDITING",
  operationId,
  caseReference,
  command,
  values,
  preparation: null,
  review: null,
  receipt: null,
  message: null,
  fieldError: null,
  requiresNewOperationId: false,
});

type EditingAction = Extract<
  OperationAction,
  { type: "EDIT" | "EDIT_REFERENCE" | "CHANGE_COMMAND" }
>;

const editingReducer = (state: OperationState, action: EditingAction): OperationState => {
  switch (action.type) {
    case "EDIT":
      return edit(state, action);
    case "EDIT_REFERENCE":
      return editReference(state, action);
    case "CHANGE_COMMAND":
      return change(state, action);
  }
};

const transitionReducer = (
  state: OperationState,
  action: Exclude<OperationAction, EditingAction>,
): OperationState => {
  switch (action.type) {
    case "PREPARING":
      return preparing(state);
    case "PREPARED":
      return {
        ...state,
        delivery: "REVIEWING",
        preparation: action.preparation,
        review: action.review,
        message: null,
        fieldError: null,
      };
    case "PREPARATION_UNKNOWN":
      return only(state, "PREPARATION_UNKNOWN", action.message);
    case "RETAINED_FOR_RECOVERY":
      return {
        ...only(state, "RETAINED_FOR_RECOVERY", action.message),
        preparation: action.preparation,
      };
    case "DEFINITELY_REJECTED":
      return reject(state, action);
    case "SUBMITTING":
      return submitting(state);
    case "ACCEPTED":
      return { ...state, delivery: "ACCEPTED", receipt: action.receipt, message: null };
    case "OUTCOME_UNKNOWN":
      return only(state, "OUTCOME_UNKNOWN", action.message);
    case "KEEP_FOR_RECOVERY":
      return retain(state);
    case "RESET_MESSAGE":
      return { ...state, message: null };
  }
};

export const operationReducer = (
  state: OperationState,
  action: OperationAction,
): OperationState => {
  if (
    action.type === "EDIT" ||
    action.type === "EDIT_REFERENCE" ||
    action.type === "CHANGE_COMMAND"
  )
    return editingReducer(state, action);
  return transitionReducer(state, action);
};
