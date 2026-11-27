import {
  submissionSelection,
  type OperationBinding,
  type OperationTaskDefinition,
} from "@contracts/client";
import { operationTask } from "../protocol/taskCatalog";
import type { FormValues } from "../protocol/forms";

type BindingBasis = Readonly<{
  selection?: Extract<OperationBinding, { kind: "WORK" }>["target"];
  values: Readonly<Record<string, string | number | undefined>>;
}>;
type RevisionDeclaration = NonNullable<
  NonNullable<OperationTaskDefinition["binding"]>["revision" | "family"]
>;
const bindingRevision = (field: RevisionDeclaration, basis: BindingBasis): number => {
  const value = Object.hasOwn(basis.values, field.input)
    ? basis.values[field.input]
    : field.initial;
  if (
    typeof value !== "number" ||
    !Number.isSafeInteger(value) ||
    value < field.minimum ||
    value > field.maximum
  ) {
    throw new Error("A declared revision basis is required.");
  }
  return value;
};
/** Interpret the closed owner plan using only explicitly supplied observed facts. */
export const initialSubmissionBinding = (
  task: OperationTaskDefinition["id"],
  basis: BindingBasis,
): OperationBinding => {
  const plan = operationTask(task).binding;
  if (plan === null) {
    throw new Error("An operation binding declaration is required.");
  }
  const binding: Record<string, unknown> = { kind: plan.kind };
  if (plan.selectsCase) {
    if (basis.selection === undefined) {
      throw new Error("A current case selection is required.");
    }
    binding["target"] = basis.selection;
  }
  for (const [member, field] of [
    ["expectedRevision", plan.revision],
    ["expectedFamilySequence", plan.family],
  ] as const) {
    if (field !== null) {
      binding[member] = bindingRevision(field, basis);
    }
  }
  if (plan.identity !== null) {
    const value = basis.values[plan.identity.input];
    if (typeof value !== "string") {
      throw new Error("A declared identity is required.");
    }
    binding[plan.identity.memberName] = value;
  }
  // oxlint-disable-next-line typescript/no-unsafe-type-assertion -- Closed generated binding plans supply exactly the members of their published binding grammar.
  return binding as OperationBinding;
};

/** Missing scope alone selects a default; contradictory or malformed alternatives never broaden. */
export const submissionScopeSelection = (values: FormValues) => {
  const scope = Object.hasOwn(values, submissionSelection.scopeInput)
    ? values[submissionSelection.scopeInput]
    : null;
  const rule = submissionSelection.rules.find(
    (candidate) =>
      candidate.hasScope === Object.hasOwn(values, submissionSelection.scopeInput) &&
      candidate.scope === scope &&
      candidate.hasId === Object.hasOwn(values, submissionSelection.caseIdInput) &&
      candidate.hasReference === Object.hasOwn(values, submissionSelection.referenceInput),
  );
  if (rule === undefined) {
    throw new Error("Invalid grant selection.");
  }
  return rule;
};

export const submissionDefaults = (
  declaration: Readonly<{ inputs: ReadonlyArray<{ readonly id: string }>; defaults: FormValues }>,
  values: FormValues,
): FormValues => {
  let result = values;
  if (declaration.inputs.some((input) => input.id === submissionSelection.scopeInput)) {
    const rule = submissionScopeSelection(values);
    result = {
      ...values,
      [submissionSelection.scopeInput]:
        rule.input === null ? submissionSelection.installationScope : submissionSelection.caseScope,
    };
  }
  return { ...declaration.defaults, ...result };
};

/** Editable authored values win; the owner's fixed-seed policy alone permits immutable seeds. */
export const mergeSubmissionInputs = (
  declaration: OperationTaskDefinition,
  values: FormValues,
  editable: ReadonlyArray<string>,
  seeds: FormValues,
): FormValues => {
  const allowed = new Set(editable);
  const merged: Record<string, FormValues[string]> = Object.fromEntries(
    Object.entries(values).filter(
      ([key]) =>
        allowed.has(key) ||
        declaration.collections.some(
          (collection) => collection.id === key && allowed.has(collection.input),
        ),
    ),
  );
  for (const [key, value] of Object.entries(seeds)) {
    if (
      !allowed.has(key) &&
      declaration.inputs.some((input) => input.id === key && input.fixedSeed)
    ) {
      merged[key] = value;
    }
  }
  return merged;
};
