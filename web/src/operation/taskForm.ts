import type { OperationAffordance } from "./affordances";
import {
  type OperationBinding,
  type OperationSubmission,
  type OperationTaskId,
  type OperationTaskDefinition,
} from "@contracts/client";
import { grantFields } from "./grantTargetDraft";

import { operationTask, taskInput } from "../protocol/taskCatalog";
import { isFormCollection, type FormValues } from "../protocol/forms";

export type TaskSubmission = {
  readonly command: OperationSubmission;
  readonly submittedRows: Readonly<Partial<Record<"peopleGrants", ReadonlyArray<string>>>>;
};

const authoredScalars = (declaration: OperationTaskDefinition, values: FormValues) => {
  values = grantFields(values);
  const inputs: Record<string, unknown> = {};
  for (const input of declaration.inputs) {
    const metadata = taskInput(input.id);
    const group = metadata.group === null ? null : `group:${metadata.group}`;
    const dormant =
      metadata.kind === "FIELD" &&
      group !== null &&
      declaration.inputs.some((candidate) => candidate.id === group) &&
      values[group] !== "UPDATE";
    if (input.source === "AUTHORED" && !dormant && Object.hasOwn(values, input.id)) {
      inputs[input.id] = values[input.id];
    }
  }
  const defaults: Readonly<Record<string, unknown>> = declaration.defaults;
  return { ...defaults, ...inputs };
};
const authoredCollections = (declaration: OperationTaskDefinition, values: FormValues) => {
  const inputs: Record<string, unknown> = {};
  const submittedRows: Partial<Record<"peopleGrants", ReadonlyArray<string>>> = {};
  for (const collection of declaration.collections) {
    const value = values[collection.id];
    if (isFormCollection(value)) {
      if (new Set(value.rows.map((row) => row.key)).size !== value.rows.length) {
        throw new Error("Collection row keys must be unique.");
      }
      submittedRows[collection.id] = Object.freeze(value.rows.map((row) => row.key));
      inputs[collection.input] = value.rows.map((row) =>
        Object.fromEntries(
          collection.inputs
            .filter((input) => Object.hasOwn(grantFields(row.values), input.id))
            .map((input) => [input.id, grantFields(row.values)[input.id]]),
        ),
      );
    }
  }
  return { inputs, submittedRows: Object.freeze(submittedRows) };
};
/** Preserve declared tokens and nulls; collection row keys remain local correlation metadata. */
export const assembleTask = (
  task: OperationTaskId,
  values: FormValues,
  binding: OperationBinding,
  operationId: string,
  control?: OperationAffordance,
): TaskSubmission => {
  const declaration = operationTask(task);
  if (control !== undefined) {
    if (control.task !== task)
      {throw new Error("The selected task does not match its Core control.");}
    const editable = new Set<string>(control.editable);
    const merged: Record<string, FormValues[string]> = Object.fromEntries(
      Object.entries(grantFields(values)).filter(
        ([key]) =>
          editable.has(key) ||
          declaration.collections.some(
            (collection) => collection.id === key && editable.has(collection.input),
          ),
      ),
    );
    for (const [key, value] of Object.entries(control.seeds)) {
      if (!editable.has(key)) {merged[key] = value;}
    }
    values = merged;
  }
  const collection = authoredCollections(declaration, values);
  const inputs = { ...authoredScalars(declaration, values), ...collection.inputs };
  // Acquisition builds a declared envelope; Core performs closed admission and business validation.
  // oxlint-disable-next-line typescript/no-unsafe-type-assertion -- Generic declaration acquisition preserves tokens; Core owns admission of the envelope.
  const command = { operationId, task, binding, inputs } as OperationSubmission;
  return { command, submittedRows: collection.submittedRows };
};
