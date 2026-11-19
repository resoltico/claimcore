import { isOperationAffordance, type OperationAffordance } from "../../src/operation/affordances";
import {
  taskDefinitions,
  type Affordance,
  type OperationTaskId,
  type OperationSubmission,
  type InputAtom,
  type HumanDocument,
} from "@contracts/client";
/** Builders supply control inputs; policy expectations remain literal in the calling tests. */
export const operationAffordance = <
  T extends OperationTaskId,
  B extends OperationSubmission["binding"],
>(
  task: T,
  binding: B,
  seeds: Readonly<Record<string, InputAtom>> = {},
  editable?: Extract<Affordance, { readonly task: NoInfer<T> }>["editable"],
  choices: Affordance["choices"] = {},
): Affordance & { readonly task: T; readonly binding: B } => {
  const declaration = taskDefinitions.find((value) => value.id === task)!;
  const authored = declaration.inputs
    .filter((value) => value.source === "AUTHORED")
    .map((value) => value.id);
  const control = {
    task,
    labelId: declaration.labelId,
    binding,
    seeds,
    editable: editable ?? [...authored, ...declaration.collections.map((value) => value.input)],
    choices,
    blocked: null,
  };
  // This test builder supplies authored bindings; decoder and native writer tests independently check the closed association.
  return { ...control, task, binding } as Affordance & { readonly task: T; readonly binding: B };
};

/** The caller explicitly authors the Core recommendation as a semantic screen region. */
export const withRecommendation = <T extends HumanDocument>(
  document: T,
  task: string | undefined,
): T => {
  const selected =
    document.actions.find((action) => action.task === task) ??
    document.screen.regions
      .filter((region) => region.role === "RECOMMENDATION")
      .flatMap((region) => region.actions ?? [])
      .find((action) => action.task === task);
  const regions = document.screen.regions.filter((region) => region.role !== "RECOMMENDATION");
  return {
    ...document,
    actions: document.actions.filter((action) => action !== selected),
    screen: {
      ...document.screen,
      regions:
        selected === undefined
          ? regions
          : [
              {
                role: "RECOMMENDATION",
                content: { kind: "ENTRIES", entries: [] },
                actions: [selected],
              },
              ...regions,
            ],
    },
  };
};

/** A malformed fixture must fail at the same operation boundary as a received control. */
export const requireOperationAffordance = (value: Affordance | undefined): OperationAffordance => {
  if (value === undefined || !isOperationAffordance(value)) {
    throw new Error("Expected an operation control.");
  }
  return value;
};

/** Review controls expose scalar submitted values; collection values remain in the submitted request. */
export const reviewSeeds = (
  inputs: OperationSubmission["inputs"],
): Readonly<Record<string, InputAtom>> => {
  const values: Record<string, InputAtom> = {};
  for (const [key, value] of Object.entries<unknown>(inputs)) {
    if (
      value === null ||
      typeof value === "string" ||
      typeof value === "number" ||
      typeof value === "boolean"
    ) {
      values[key] = value;
    }
  }
  return values;
};
