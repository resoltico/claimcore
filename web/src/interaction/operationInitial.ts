import type { CommandKind, DraftValues } from "./metadata";
import type { OperationState } from "./operationState";

export const initialOperation = (
  operationId: string,
  command: CommandKind,
  values: DraftValues,
  caseReference: string,
): OperationState => ({
  delivery: "EDITING",
  unresolved: false,
  disclosureRefused: false,
  operationId,
  caseReference,
  command,
  values,

  requestBytes: null,
  prepared: null,
  review: null,
  receipt: null,
  message: null,
  fieldError: null,
  exposedRequest: null,
  pending: null,
});
