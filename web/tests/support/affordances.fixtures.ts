import {
  taskDefinitions,
  type Affordance,
  type OperationTaskId,
  type TaskInputId,
} from "@contracts/client";
/** Builders supply control inputs; policy expectations remain literal in the calling tests. */
export const operationAffordance = <T extends OperationTaskId>(
  task: T,
  binding: Extract<Affordance, { task: T }>["binding"],
  seeds: Readonly<Record<string, unknown>> = {},
  editable?: ReadonlyArray<TaskInputId>,
  choices: Readonly<Record<string, ReadonlyArray<string>>> = {},
  primary = false,
): Extract<Affordance, { task: T }> => {
  const declaration = taskDefinitions.find((value) => value.id === task)!;
  const authored = declaration.inputs
    .filter(
      (value) =>
        value.source === "AUTHORED" || (task === "case.open" && value.id === "field:caseReference"),
    )
    .map((value) => value.id);
  return {
    task,
    binding,
    seeds,
    editable: editable ?? [...authored, ...declaration.collections.map((value) => value.input)],
    choices,
    primary,
    blocked: null,
  } as Extract<Affordance, { task: T }>;
};
