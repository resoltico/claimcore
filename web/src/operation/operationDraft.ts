import { grantFields, grantTarget, withGrantTarget, isGrantTarget } from "./grantTargetDraft";
import type { OperationAffordance } from "./affordances";
import { sameDateEntryDraft, withoutDateEntries } from "./dateEntryDraft";
import { inputChoiceAllowed } from "./inputChoices";
import { operationEntry, type OperationSelection } from "../protocol/operationCatalog";
import type { OperationAction, OperationDraft } from "./operationTypes";

import type { FormValue, FormValues } from "../protocol/forms";
import type { OwnerScope } from "../protocol/ownerScope";
import type { OperationBinding } from "@contracts/client";

/** Selecting installation scope removes the case selection from the authored grant. */
export const editGrantInput = (values: FormValues, input: string, value: FormValue): FormValues => {
  if (input === "input:scope") {
    if (isGrantTarget(value)) {
      return withGrantTarget(values, value);
    }
    if (value === "INSTALLATION" || value === "CASE") {
      return withGrantTarget(values, {
        kind: "GRANT_TARGET",
        selection:
          value === "INSTALLATION" ? { kind: "INSTALLATION" } : { kind: "REFERENCE", text: "" },
      });
    }
  }
  if ((input === "input:caseId" || input === "field:caseReference") && typeof value === "string") {
    return withGrantTarget(values, {
      kind: "GRANT_TARGET",
      selection: { kind: input === "input:caseId" ? "ID" : "REFERENCE", text: value },
    });
  }
  return { ...values, [input]: value };
};

export const createDraft = (
  entry: OperationSelection,
  target: string,
  binding: OperationBinding,
  authored: FormValues,
  owner: OwnerScope | null,
  affordance: OperationAffordance | null,
): OperationDraft => {
  const values = operationEntry(entry).inputs.some((input) => input.id === "input:scope")
    ? withGrantTarget(authored, grantTarget(authored))
    : { ...authored };
  return {
    owner,
    affordance,
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
const invalidatedChoices = (draft: OperationDraft, action: Edit): FormValues => {
  const values = grantFields(editGrantInput(draft.values, action.path, action.value));
  return Object.fromEntries(
    operationEntry(draft.entry).inputs.flatMap((input) => {
      const selected = values[input.id];
      return typeof selected === "string" &&
        input.choicesWhen.some((rule) => rule.input === action.path) &&
        !inputChoiceAllowed(input, selected, values)
        ? [[input.id, ""]]
        : [];
    }),
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
      ...(draft.entry.subject === "PEOPLE"
        ? editGrantInput(draft.values, action.path, action.value)
        : { ...draft.values, [action.path]: action.value }),
      ...invalidatedChoices(draft, action),
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
