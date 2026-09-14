import type { CommandKind, DraftValues } from "./metadata";
import type { OperationState } from "./operationReducer";

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
  exposedRequest: null,
  pending: null,
});
