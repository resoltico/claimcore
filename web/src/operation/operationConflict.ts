import type {
  OperationInput,
  CaseSelector,
  ReminderAct,
  OperationTaskDefinition,
} from "@contracts/client";
import type { TaskBindingSource } from "./taskForm";
import type {
  ConflictSource,
  LocalSource,
  OperationAction,
  RevisionConflictSnapshot,
} from "./operationTypes";
const caseBound = (source: LocalSource): source is ConflictSource =>
  source.prepared.input.kind !== "AUTHORITY";
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
  return { identity: action.identity, source, diagnostic: action.rejection, latest: null };
};
export const rebasedTaskBinding = (input: OperationInput, revision: number): TaskBindingSource => {
  switch (input.kind) {
    case "CASE":
      return {
        kind: "CASE",
        target: input.target,
        revision,
        ...(input.act.kind === "REMOVE_PAYMENT" ? { paymentId: input.act.paymentId } : {}),
      };
    case "WORK":
      return { kind: "WORK", target: input.target, workRevision: revision };
    case "REMINDER":
      if (input.act.kind === "CREATE") {
        throw new Error("A new reminder has no prior version to rebase.");
      }
      return {
        kind: "REMINDER",
        target: input.target,
        reminder: { reminderId: input.act.reminderId, revision },
      };
    case "NOTE":
      return {
        kind: "NOTE",
        target: input.target,
        ...(input.act.kind === "REMOVE_NOTE" ? { noteId: input.act.noteId } : {}),
      };
    case "AUTHORITY":
      throw new Error("Authority commands do not have a reviewed version.");
  }
};
const sameSelector = (left: CaseSelector, right: CaseSelector): boolean =>
  left.kind === "ID"
    ? right.kind === "ID" && left.caseId === right.caseId
    : right.kind === "REFERENCE" && left.caseReference === right.caseReference;
const sameReminder = (before: ReminderAct, after: ReminderAct): boolean =>
  before.kind !== "CREATE" && after.kind !== "CREATE" && before.reminderId === after.reminderId;
/** Target spelling and selected reminder identity cannot change as a side effect of rebasing. */
export const sameConflictTarget = (before: OperationInput, after: OperationInput): boolean => {
  if (
    before.kind === "AUTHORITY" ||
    after.kind === "AUTHORITY" ||
    before.kind !== after.kind ||
    before.act.kind !== after.act.kind
  ) {
    return false;
  }
  if (!sameSelector(before.target, after.target)) {
    return false;
  }
  return before.kind === "REMINDER" && after.kind === "REMINDER"
    ? sameReminder(before.act, after.act)
    : true;
};
