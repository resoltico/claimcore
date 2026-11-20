import { caseLinkTarget } from "../routing/coreLinks";
import { isOperationAffordance } from "./affordances";
import {
  type CaseSelector,
  type OperationSubmission,
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
  "target" in source.prepared.input.binding;
const stableConflictTarget = (
  diagnostic: NonNullable<RevisionConflictSnapshot["diagnostic"]>,
  previous: CaseSelector,
  knownCaseId: string | undefined,
): RevisionConflictSnapshot["target"] | null => {
  const offered = diagnostic.document.links.filter(({ rel }) => rel === "OPEN_CASE");
  const target = offered.length === 1 ? caseLinkTarget(offered[0]!.link) : undefined;
  if (
    target?.kind !== "ID" ||
    (previous.kind === "ID" && previous.caseId !== target.caseId) ||
    (knownCaseId !== undefined && knownCaseId !== target.caseId)
  ) {
    return null;
  }
  return target;
};
export const conflictFromRefusal = (
  action: Extract<OperationAction, { type: "REFUSE" }>,
  source: LocalSource | null,
  entry: OperationTaskDefinition,
  uncertain: boolean,
  knownCaseId: string | undefined,
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
  const target = stableConflictTarget(
    action.rejection,
    source.prepared.input.binding.target,
    knownCaseId,
  );
  if (target === null) {
    return null;
  }
  return {
    identity: action.identity,
    target,
    source,
    minimumRevision: action.rejection.actualRevision,
    diagnostic: action.rejection,
    latest: null,
  };
};
export const rebasedOperationBinding = (
  input: OperationSubmission,
  target: RevisionConflictSnapshot["target"],
  revision: number,
  currentActions: ReadonlyArray<Affordance>,
): OperationBinding => {
  const { binding } = input;
  if ("expectedFamilySequence" in binding) {
    const current = currentActions.find(
      (action) => action.task === input.task && action.blocked === null,
    );
    if (
      current === undefined ||
      !isOperationAffordance(current) ||
      current.task !== input.task ||
      current.binding.kind !== "CASE" ||
      !("expectedFamilySequence" in current.binding)
    ) {
      throw new Error("A fresh reviewed action is required.");
    }
    return {
      ...binding,
      target,
      expectedRevision: current.binding.expectedRevision,
      expectedFamilySequence: current.binding.expectedFamilySequence,
    };
  }
  return "target" in binding && "expectedRevision" in binding
    ? { ...binding, target, expectedRevision: revision }
    : binding;
};
const sameChild = (left: OperationBinding, right: OperationBinding): boolean => {
  if ("partyId" in left) {
    return "partyId" in right && left.partyId === right.partyId;
  }
  if ("reminderId" in left) {
    return "reminderId" in right && left.reminderId === right.reminderId;
  }
  if ("linkId" in left) {
    return "linkId" in right && left.linkId === right.linkId;
  }
  if ("noteId" in left) {
    return "noteId" in right && left.noteId === right.noteId;
  }
  if ("paymentId" in left) {
    return "paymentId" in right && left.paymentId === right.paymentId;
  }
  return true;
};
/** Rebasing preserves the selected entity and operation task. */
export const sameConflictTarget = (
  before: OperationSubmission,
  after: OperationSubmission,
  target: RevisionConflictSnapshot["target"],
): boolean => {
  const left = before.binding;
  const right = after.binding;
  if (
    !("target" in left) ||
    !("target" in right) ||
    left.kind !== right.kind ||
    before.task !== after.task ||
    right.target.kind !== "ID" ||
    right.target.caseId !== target.caseId
  ) {
    return false;
  }
  return sameChild(left, right);
};
