import type { OperationAction } from "./operationReducer";

export type EditingAction = Extract<
  OperationAction,
  {
    type: "EDIT" | "EDIT_CORRECTION" | "SET_CORRECTION_MODE" | "EDIT_REFERENCE" | "CHANGE_COMMAND";
  }
>;

export type PrepareResponse = Extract<
  OperationAction,
  { type: "PREPARED" | "PREPARATION_UNKNOWN" | "RETAINED_FOR_RECOVERY" }
>;

export type CompletionResponse = Extract<
  OperationAction,
  { type: "DEFINITELY_REJECTED" | "ACCEPTED" | "OUTCOME_UNKNOWN" }
>;
