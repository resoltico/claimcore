import {
  type OperationSubmission,
  type CaseSelector,
  type OperationTaskDefinition,
  type OperationBinding,
  type Affordance,
} from "@contracts/client";

import type {
  ConflictSource,
  LocalSource,
  OperationAction,
  RevisionConflictSnapshot,
} from "./operationTypes";
const caseBound = (source: LocalSource): source is ConflictSource =>
  source.prepared.input.binding.kind !== "PEOPLE";
export const conflictFromRefusal = (
  action: Extract<OperationAction, { type: "REFUSE" }>,
  source: LocalSource | null,
  entry: OperationTaskDefinition,
  uncertain: boolean,
): RevisionConflictSnapshot | null => {
  if (
    uncertain ||
    action.identity === undefined ||
    source === null ||
    !caseBound(source) ||
    action.rejection?.kind !== "CONFLICT" ||
    !entry.inputs.some((input) => input.source === "EXPECTED_REVISION")
  ) {
    return null;
  }
  return {
    identity: action.identity,
    source,
    minimumRevision: action.rejection.actualRevision,
    diagnostic: action.rejection,
    latest: null,
  };
};
export const rebasedOperationBinding = (
  input: OperationSubmission,
  revision: number,
  currentActions: ReadonlyArray<Affordance>,
): OperationBinding => {
  const { binding } = input;
  if (input.task === "case.erase") {
    const current = currentActions.find(
      (action) => action.task === "case.erase" && action.blocked === null,
    );
    if (current?.task !== "case.erase") {
      throw new Error("A fresh erasure action is required.");
    }
    return {
      ...input.binding,
      expectedRevision: current.binding.expectedRevision,
      expectedFamilySequence: current.binding.expectedFamilySequence,
    };
  }
  return binding.kind !== "PEOPLE" && "expectedRevision" in binding
    ? { ...binding, expectedRevision: revision }
    : binding;
};
const sameSelector = (left: CaseSelector, right: CaseSelector): boolean =>
  left.kind === "ID"
    ? right.kind === "ID" && left.caseId === right.caseId
    : right.kind === "REFERENCE" && left.caseReference === right.caseReference;
/** Rebasing preserves the selected entity and operation task. */
export const sameConflictTarget = (
  before: OperationSubmission,
  after: OperationSubmission,
): boolean => {
  const left = before.binding;
  const right = after.binding;
  if (
    left.kind === "PEOPLE" ||
    right.kind === "PEOPLE" ||
    left.kind !== right.kind ||
    before.task !== after.task ||
    !sameSelector(left.target, right.target)
  ) {
    return false;
  }
  if (left.kind === "REMINDER" && right.kind === "REMINDER") {
    return (
      !("reminderId" in left) || ("reminderId" in right && left.reminderId === right.reminderId)
    );
  }
  return true;
};
