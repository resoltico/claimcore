import { mergeSubmissionInputs, submissionDefaults } from "./submissionAssembly";
import type { OperationAffordance } from "./affordances";
import {
  type OperationBinding,
  type OperationSubmission,
  type OperationTaskId,
  type OperationTaskDefinition,
} from "@contracts/client";
import { grantFields } from "./grantTargetDraft";

import { operationTask } from "../protocol/taskCatalog";
import { isFormCollection, type FormValues } from "../protocol/forms";

type CollectionId = OperationTaskDefinition["collections"][number]["id"];

export type TaskSubmission = {
  readonly command: OperationSubmission;
  readonly submittedRows: Readonly<Partial<Record<CollectionId, ReadonlyArray<string>>>>;
};

const authoredScalars = (declaration: OperationTaskDefinition, values: FormValues) => {
  values = grantFields(values);
  const inputs: Record<string, FormValues[string]> = {};
  for (const input of declaration.inputs) {
    const dormant =
      input.activeWhen !== null && values[input.activeWhen.input] !== input.activeWhen.value;
    if (input.source === "AUTHORED" && !dormant && Object.hasOwn(values, input.id)) {
      inputs[input.id] = values[input.id]!;
    }
  }
  return submissionDefaults(declaration, inputs);
};
const authoredCollections = (declaration: OperationTaskDefinition, values: FormValues) => {
  const inputs: Record<string, unknown> = {};
  const submittedRows: Partial<Record<CollectionId, ReadonlyArray<string>>> = {};
  for (const collection of declaration.collections) {
    const value = values[collection.id];
    if (isFormCollection(value)) {
      if (new Set(value.rows.map((row) => row.key)).size !== value.rows.length) {
        throw new Error("Collection row keys must be unique.");
      }
      submittedRows[collection.id] = Object.freeze(value.rows.map((row) => row.key));
      inputs[collection.input] = value.rows.map((row) => {
        const fields = submissionDefaults(collection, grantFields(row.values));
        return Object.fromEntries(
          collection.inputs
            .filter((input) => Object.hasOwn(fields, input.id))
            .map((input) => [input.id, fields[input.id]]),
        );
      });
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
    if (control.task !== task) {
      throw new Error("The selected task does not match its Core control.");
    }
    values = mergeSubmissionInputs(
      declaration,
      grantFields(values),
      control.editable,
      control.seeds,
    );
  }
  const collection = authoredCollections(declaration, values);
  const inputs = { ...authoredScalars(declaration, values), ...collection.inputs };
  // Acquisition builds a declared envelope; Core performs closed admission and business validation.
  // oxlint-disable-next-line typescript/no-unsafe-type-assertion -- Generic declaration acquisition preserves tokens; Core owns admission of the envelope.
  const command = { operationId, task, binding, inputs } as OperationSubmission;
  return { command, submittedRows: collection.submittedRows };
};
