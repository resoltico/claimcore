import { sameDateEntryDraft, withoutDateEntries } from "./dateEntryDraft";
import { roleAdmitted } from "./roleAvailability";
import type { OperationAction, OperationDraft } from "./operationTypes";
import type { OperationSelection } from "../protocol/operationCatalog";
import type { FormValues } from "../protocol/forms";
import type { OwnerScope } from "../protocol/ownerScope";
import type { TaskBindingSource } from "./taskForm";

export const createDraft = (
  entry: OperationSelection,
  target: string,
  binding: TaskBindingSource,
  authored: FormValues,
  owner: OwnerScope | null,
): OperationDraft => {
  const values = { ...authored };
  if (binding.kind === "PEOPLE" && binding.principal !== undefined) {
    delete values["input:principal.kind"];
    delete values["input:principal.issuer"];
    delete values["input:principal.subject"];
  }
  if (entry.kind === "people.revoke" && binding.kind === "PEOPLE" && binding.grant !== undefined) {
    delete values["input:role"];
    delete values["input:scope"];
    delete values["field:caseReference"];
  }
  return {
    owner,
    entry,
    target,
    binding,
    values,
    baseline: values,
    dateEntries: {},
    generation: 0,
    pendingDiscard: null,
  };
};

type Edit = Extract<OperationAction, { type: "EDIT" }>;
const clearsRoleSelection = (draft: OperationDraft, action: Edit): boolean => {
  if (
    draft.entry.subject !== "PEOPLE" ||
    draft.entry.kind !== "people.grant" ||
    (action.path !== "input:principal.kind" && action.path !== "input:scope")
  ) {
    return false;
  }
  const values = { ...draft.values, [action.path]: action.value };
  const role = values["input:role"];
  const principal = values["input:principal.kind"];
  const scope = values["input:scope"];
  return (
    typeof role === "string" &&
    typeof principal === "string" &&
    !roleAdmitted(role, principal, typeof scope === "string" ? scope : undefined)
  );
};
export const editDraft = (draft: OperationDraft, action: Edit): OperationDraft => {
  const dateEntries = withoutDateEntries(draft.dateEntries, [action.path]);
  if (draft.values[action.path] === action.value) {
    return dateEntries === draft.dateEntries ? draft : { ...draft, dateEntries };
  }
  return {
    ...draft,
    dateEntries,
    generation: draft.generation + 1,
    values: {
      ...draft.values,
      [action.path]: action.value,
      ...(clearsRoleSelection(draft, action) ? { "input:role": "" } : {}),
    },
  };
};
export const editDraftFields = (
  draft: OperationDraft,
  action: Extract<OperationAction, { type: "EDIT_FIELDS" }>,
): OperationDraft => {
  const values = { ...draft.values, ...action.values };
  for (const path of action.omitted) {
    delete values[path];
  }
  const changed =
    action.omitted.some((path) => Object.hasOwn(draft.values, path)) ||
    Object.entries(action.values).some(([path, value]) => draft.values[path] !== value);
  const dateEntries = withoutDateEntries(draft.dateEntries, [
    ...Object.keys(action.values),
    ...action.omitted,
  ]);
  if (changed) {
    return { ...draft, values, dateEntries, generation: draft.generation + 1 };
  }
  return dateEntries === draft.dateEntries ? draft : { ...draft, dateEntries };
};
export const captureDraftDate = (
  draft: OperationDraft,
  action: Extract<OperationAction, { type: "DATE_ENTRY" }>,
): OperationDraft => {
  if (action.parts === null || action.parts.length === 0) {
    const dateEntries = withoutDateEntries(draft.dateEntries, [action.path]);
    return dateEntries === draft.dateEntries ? draft : { ...draft, dateEntries };
  }
  const next =
    action.canonical === ""
      ? draft
      : editDraft(draft, { type: "EDIT", path: action.path, value: "" });
  const entry = { canonical: "", parts: action.parts };
  return sameDateEntryDraft(next.dateEntries[action.path], entry)
    ? next
    : { ...next, dateEntries: { ...next.dateEntries, [action.path]: entry } };
};
export const clearDraftDates = (draft: OperationDraft): OperationDraft =>
  Object.keys(draft.dateEntries).length === 0 ? draft : { ...draft, dateEntries: {} };
