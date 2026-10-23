import type { OperationReceipt, OperationReview } from "../api/types";
import type { Notice } from "../api/notices";
import type { FormValues } from "../protocol/forms";
import type { PreparedRequest } from "../protocol/preparedRequest";
import { refusalPath } from "./operationForm";
export type OperationState = {
  target: string;
  disclosureToken: string;
  disclosureRefused: boolean;
  phase: "EDITING" | "PREVIEWING" | "REVIEWING" | "SUBMITTING" | "ACCEPTED";
  values: FormValues;
  baseline: FormValues;
  prepared: PreparedRequest<"operation.execute"> | null;
  priorUncertain: boolean;
  review: OperationReview | null;
  receipt: OperationReceipt | null;
  notice: Notice | null;
  fieldError: string | null;
  confirmed: boolean;
};
export type OperationAction =
  | { type: "RESET"; state: OperationState }
  | { type: "EDIT"; path: string; value: string | boolean; id: string }
  | { type: "INVALID_INPUT"; path: string | null }
  | { type: "PREVIEW"; prepared: PreparedRequest<"operation.execute"> }
  | { type: "REVIEW"; review: OperationReview; token: string }
  | { type: "RECORD"; receipt: OperationReceipt; token: string }
  | {
      type: "REFUSE";
      notice: Notice;
      target: string | null;
      uncertain: boolean;
      disclosure: boolean;
    }
  | { type: "INVALIDATE"; uncertain: boolean; refused: boolean }
  | { type: "RETURN" }
  | { type: "CONFIRM"; value: boolean }
  | { type: "EXECUTE" };
const canEdit = (state: OperationState): boolean =>
  !state.priorUncertain && ["EDITING", "REVIEWING"].includes(state.phase);
const canConfirm = (state: OperationState): boolean =>
  state.phase === "REVIEWING" &&
  state.review !== null &&
  !state.priorUncertain &&
  !state.disclosureRefused;
const edited = (
  state: OperationState,
  action: Extract<OperationAction, { type: "EDIT" }>,
): OperationState => {
  if (!canEdit(state) || state.values[action.path] === action.value) {
    return state;
  }
  return {
    ...state,
    phase: "EDITING",
    prepared: null,
    review: null,
    receipt: null,
    values: {
      ...state.values,
      [action.path]: action.value,
      operationId: state.prepared === null ? state.values["operationId"]! : action.id,
    },
    notice: null,
    fieldError: null,
    confirmed: false,
  };
};
const previewed = (
  state: OperationState,
  action: Extract<OperationAction, { type: "PREVIEW" }>,
): OperationState => {
  if (!canEdit(state)) {
    return state;
  }
  return {
    ...state,
    phase: "PREVIEWING",
    prepared: action.prepared,
    review: null,
    notice: null,
    fieldError: null,
    confirmed: false,
  };
};
const reviewed = (
  state: OperationState,
  action: Extract<OperationAction, { type: "REVIEW" }>,
): OperationState => {
  if (state.phase === "PREVIEWING") {
    return {
      ...state,
      phase: "REVIEWING",
      review: action.review,
      disclosureToken: action.token,
      disclosureRefused: false,
      confirmed: false,
    };
  }
  return state;
};
const recorded = (
  state: OperationState,
  action: Extract<OperationAction, { type: "RECORD" }>,
): OperationState => ({
  ...state,
  phase: "ACCEPTED",
  receipt: action.receipt,
  priorUncertain: false,
  disclosureToken: action.token,
  disclosureRefused: false,
  notice: null,
  confirmed: false,
});
const refused = (
  state: OperationState,
  action: Extract<OperationAction, { type: "REFUSE" }>,
): OperationState => {
  const priorUncertain = state.priorUncertain || action.uncertain;
  return {
    ...state,
    phase: priorUncertain ? "REVIEWING" : "EDITING",
    priorUncertain,
    notice: action.notice,
    review: null,
    confirmed: false,
    fieldError: refusalPath(action.target, state.values),
    disclosureRefused: action.disclosure,
  };
};
const invalidated = (
  state: OperationState,
  action: Extract<OperationAction, { type: "INVALIDATE" }>,
): OperationState => ({
  ...state,
  phase: "EDITING",
  priorUncertain: state.priorUncertain || action.uncertain,
  disclosureRefused: action.refused,
  review: null,
  receipt: null,
  confirmed: false,
});
const returnToEdit = (state: OperationState): OperationState =>
  canEdit(state) ? { ...state, phase: "EDITING", review: null, confirmed: false } : state;
const confirmed = (state: OperationState, value: boolean): OperationState =>
  canConfirm(state) ? { ...state, confirmed: value } : state;
const executing = (state: OperationState): OperationState => {
  if (
    state.prepared === null ||
    (!state.priorUncertain && (!canConfirm(state) || !state.confirmed))
  ) {
    return state;
  }
  return { ...state, phase: "SUBMITTING", notice: null };
};
/** Acceptance alone resolves an earlier uncertain execution; editing never rebases frozen bytes. */
export const operationReducer = (
  state: OperationState,
  action: OperationAction,
): OperationState => {
  switch (action.type) {
    case "RESET":
      return action.state;
    case "EDIT":
      return edited(state, action);
    case "INVALID_INPUT":
      return {
        ...state,
        notice: { kind: "local", reason: "invalidInput" },
        fieldError: action.path,
      };
    case "PREVIEW":
      return previewed(state, action);
    case "REVIEW":
      return reviewed(state, action);
    case "RECORD":
      return recorded(state, action);
    case "REFUSE":
      return refused(state, action);
    case "INVALIDATE":
      return invalidated(state, action);
    case "RETURN":
      return returnToEdit(state);
    case "CONFIRM":
      return confirmed(state, action.value);
    case "EXECUTE":
      return executing(state);
  }
};
