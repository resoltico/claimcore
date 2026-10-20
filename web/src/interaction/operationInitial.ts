import type { CommandKind, DraftValues } from "./metadata";
import { projectOperation, type OperationState } from "./operationState";

export const initialOperation = (
  operationId: string,
  command: CommandKind,
  values: DraftValues,
  caseReference: string,
): OperationState =>
  projectOperation({
    mutation: { phase: "EDITING", prepared: null },
    disclosureRefused: false,
    operationId,
    caseReference,
    command,
    values,

    review: null,
    receipt: null,
    message: null,
    fieldError: null,
  });
