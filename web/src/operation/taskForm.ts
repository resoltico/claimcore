import type {
  OperationBinding,
  OperationSubmission,
  OperationTaskId,
  OperationTaskDefinition,
} from "@contracts/client";
import { operationTask, taskInput } from "../protocol/taskCatalog";
import { isFormCollection, type FormValues } from "../protocol/forms";

export type TaskSubmission = {
  readonly command: OperationSubmission;
  readonly submittedRows: Readonly<Partial<Record<"peopleGrants", ReadonlyArray<string>>>>;
};

const authoredScalars = (declaration: OperationTaskDefinition, values: FormValues) => {
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
  return inputs;
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
            .filter((input) => Object.hasOwn(row.values, input.id))
            .map((input) => [input.id, row.values[input.id]]),
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
): TaskSubmission => {
  const declaration = operationTask(task);
  const collection = authoredCollections(declaration, values);
  const inputs = { ...authoredScalars(declaration, values), ...collection.inputs };
  const selected =
    binding.kind === "CASE" && task === "case.open"
      ? {
          ...binding,
          target: { kind: "REFERENCE", caseReference: values["field:caseReference"] ?? "" },
        }
      : binding;
  // Acquisition builds a declared envelope; Core performs closed admission and business validation.
  // oxlint-disable-next-line typescript/no-unsafe-type-assertion -- Generic declaration acquisition preserves tokens; Core owns admission of the envelope.
  const command = { operationId, task, binding: selected, inputs } as OperationSubmission;
  return { command, submittedRows: collection.submittedRows };
};
