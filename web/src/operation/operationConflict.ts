import type { OperationInput, CaseSelector, ReminderAct } from "@contracts/client";
import type { FormValues } from "../protocol/forms";
import type { CatalogEntry } from "../protocol/catalogAssembly";
import { bindingPath } from "../protocol/catalogAssembly";
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
  entry: CatalogEntry,
  uncertain: boolean,
): RevisionConflictSnapshot | null => {
  if (uncertain || action.identity === undefined || source === null || !caseBound(source)) {
    return null;
  }
  if (
    action.notice.kind !== "diagnostic" ||
    action.notice.diagnostic.code !== "REVISION_CONFLICT"
  ) {
    return null;
  }
  if (!entry.inputs.some((input) => input.source === "EXPECTED_REVISION")) {
    return null;
  }
  return { identity: action.identity, source, diagnostic: action.notice.diagnostic, latest: null };
};
/** Re-preparation changes only request identity and the core-declared assumption binding. */
export const latestOperationValues = (
  entry: CatalogEntry,
  values: FormValues,
  revision: number,
  id: string,
): FormValues => ({
  ...values,
  ...Object.fromEntries(
    entry.inputs.flatMap((input) => {
      if (input.source === "EXPECTED_REVISION") {
        return [[bindingPath(input), String(revision)]];
      }
      return input.source === "OPERATION_IDENTITY" ? [[bindingPath(input), id]] : [];
    }),
  ),
});
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
