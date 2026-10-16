import type { OperationAction } from "./operationState";

export type EditingAction = Extract<
  OperationAction,
  {
    type: "EDIT" | "EDIT_CORRECTION" | "SET_CORRECTION_MODE" | "EDIT_REFERENCE" | "CHANGE_COMMAND";
  }
>;

export type CompletionResponse = Extract<
  OperationAction,
  { type: "DEFINITELY_REJECTED" | "ACCEPTED" | "OUTCOME_UNKNOWN" }
>;
