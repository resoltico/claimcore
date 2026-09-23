import type { Notice } from "../api/notices";
import type { AdvisoryReview, CommandDraft, PreparationDetails, Receipt } from "../api/v2";
import type { CommandKind, CorrectionGroupName, CorrectionMode, DraftValues } from "./metadata";
import { isCorrectionValues } from "./metadata";
import { freezeRequest } from "./operationRequest";
import type { CompletionResponse, EditingAction, PrepareResponse } from "./operationReducerTypes";

export { initialOperation } from "./operationInitial";

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
  message: Notice | null;
  fieldError: { name: string; message: Notice } | null;
  exposedRequest: CommandDraft | null;
  pending: { kind: "PREPARE" | "SUBMIT"; requestId: number } | null;
};
export type OperationAction =
  | { type: "EDIT"; field: string; value: string; nextOperationId: string }
  | {
      type: "EDIT_CORRECTION";
      group: CorrectionGroupName;
      field: string;
      value: string;
      nextOperationId: string;
    }
  | {
      type: "SET_CORRECTION_MODE";
      group: CorrectionGroupName;
      mode: CorrectionMode;
      nextOperationId: string;
    }
  | { type: "EDIT_REFERENCE"; value: string; nextOperationId: string }
  | { type: "CHANGE_COMMAND"; command: CommandKind; values: DraftValues; nextOperationId: string }
  | { type: "PREPARING"; requestId: number; draft: CommandDraft }
  | { type: "PREPARED"; requestId: number; preparation: PreparationDetails; review: AdvisoryReview }
  | { type: "PREPARATION_UNKNOWN"; requestId: number; message: Notice }
  | {
      type: "RETAINED_FOR_RECOVERY";
      requestId: number;
      preparation: PreparationDetails;
      message: Notice;
    }
  | { type: "DEFINITELY_REJECTED"; requestId: number; message: Notice; field: string | null }
  | { type: "SUBMITTING"; requestId: number }
  | { type: "ACCEPTED"; requestId: number; receipt: Receipt }
  | { type: "OUTCOME_UNKNOWN"; requestId: number; message: Notice }
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
  if (!editable(state) || !isCorrectionValues(state.values)) return state;
  const group = state.values[action.group];
  if (group.mode !== "REPLACE" || group.values[action.field] === action.value) return state;
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
  if (!editable(state) || !isCorrectionValues(state.values)) return state;
  const group = state.values[action.group];
  if (group.mode === action.mode) return state;
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
  !editable(state) ? state : reset(state, action.values, action.nextOperationId, action.command);
const only = (
  state: OperationState,
  delivery: DeliveryState,
  message: Notice | null,
): OperationState => ({ ...state, delivery, message, fieldError: null });
const preparing = (
  state: OperationState,
  action: Extract<OperationAction, { type: "PREPARING" }>,
) => {
  if (!editable(state) && state.delivery !== "PREPARATION_UNKNOWN") return state;
  if (state.exposedRequest !== null && state.exposedRequest !== action.draft) return state;
  if (action.draft.operationId !== state.operationId) return state;
  return {
    ...only(state, "PREPARING", null),
    exposedRequest: state.exposedRequest ?? freezeRequest(action.draft),
    pending: { kind: "PREPARE" as const, requestId: action.requestId },
  };
};
const submitting = (
  state: OperationState,
  action: Extract<OperationAction, { type: "SUBMITTING" }>,
) =>
  state.delivery === "REVIEWING" && state.exposedRequest !== null
    ? {
        ...only(state, "SUBMITTING", null),
        pending: { kind: "SUBMIT" as const, requestId: action.requestId },
      }
    : state;
const retain = (state: OperationState): OperationState =>
  state.delivery !== "REVIEWING"
    ? state
    : {
        ...state,
        delivery: "EDITING",
        preparation: null,
        review: null,
        pending: null,
      };

const reject = (
  state: OperationState,
  action: Extract<OperationAction, { type: "DEFINITELY_REJECTED" }>,
): OperationState => ({
  ...only(state, "DEFINITELY_REJECTED", action.message),
  fieldError: action.field === null ? null : { name: action.field, message: action.message },
  pending: null,
});

const matchesPending = (state: OperationState, kind: "PREPARE" | "SUBMIT", requestId: number) =>
  state.pending?.kind === kind && state.pending.requestId === requestId;

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

const prepareResponse = (state: OperationState, action: PrepareResponse): OperationState => {
  if (!matchesPending(state, "PREPARE", action.requestId)) return state;
  switch (action.type) {
    case "PREPARED":
      return {
        ...state,
        delivery: "REVIEWING",
        preparation: action.preparation,
        review: action.review,
        message: null,
        fieldError: null,
        pending: null,
      };
    case "PREPARATION_UNKNOWN":
      return { ...only(state, "PREPARATION_UNKNOWN", action.message), pending: null };
    case "RETAINED_FOR_RECOVERY":
      return {
        ...only(state, "RETAINED_FOR_RECOVERY", action.message),
        preparation: action.preparation,
        pending: null,
      };
  }
};

const completionResponse = (state: OperationState, action: CompletionResponse): OperationState => {
  if (state.pending?.requestId !== action.requestId) return state;
  switch (action.type) {
    case "DEFINITELY_REJECTED":
      return reject(state, action);
    case "ACCEPTED":
      return {
        ...state,
        delivery: "ACCEPTED",
        receipt: action.receipt,
        message: null,
        pending: null,
      };
    case "OUTCOME_UNKNOWN":
      return state.pending.kind === "SUBMIT"
        ? { ...only(state, "OUTCOME_UNKNOWN", action.message), pending: null }
        : state;
  }
};

const transitionReducer = (
  state: OperationState,
  action: Exclude<OperationAction, EditingAction>,
): OperationState => {
  switch (action.type) {
    case "PREPARING":
      return preparing(state, action);
    case "SUBMITTING":
      return submitting(state, action);
    case "KEEP_FOR_RECOVERY":
      return retain(state);
    case "RESET_MESSAGE":
      return { ...state, message: null };
    case "PREPARED":
    case "PREPARATION_UNKNOWN":
    case "RETAINED_FOR_RECOVERY":
      return prepareResponse(state, action);
    case "DEFINITELY_REJECTED":
    case "ACCEPTED":
    case "OUTCOME_UNKNOWN":
      return completionResponse(state, action);
  }
};

export const operationReducer = (
  state: OperationState,
  action: OperationAction,
): OperationState => {
  if (
    action.type === "EDIT" ||
    action.type === "EDIT_CORRECTION" ||
    action.type === "SET_CORRECTION_MODE" ||
    action.type === "EDIT_REFERENCE" ||
    action.type === "CHANGE_COMMAND"
  )
    return editingReducer(state, action);
  return transitionReducer(state, action);
};
