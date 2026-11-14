import type {
  Affordance,
  HumanDocument,
  OperationTaskId,
  OperationTaskDefinition,
} from "@contracts/client";
import { operationTask } from "../protocol/taskCatalog";
import type { FormValue, FormValues } from "../protocol/forms";
export type OperationAffordance = Extract<Affordance, { task: OperationTaskId }>;
export const operationActions = (actions: ReadonlyArray<Affordance>) =>
  actions.filter(
    (action): action is OperationAffordance => action.task !== "people.requests.dismiss",
  );
export const allActions = (document: HumanDocument): ReadonlyArray<Affordance> => [
  ...document.actions,
  ...document.screen.regions.flatMap((region) => [
    ...(region.actions ?? []),
    ...(region.content.kind === "TABLE"
      ? region.content.rows.flatMap((row) => row.actions ?? [])
      : []),
    ...(region.content.kind === "TIMELINE"
      ? region.content.entries.flatMap((row) => row.actions ?? [])
      : []),
    ...(region.content.kind === "STAGES"
      ? region.content.steps.flatMap((row) => row.actions ?? [])
      : []),
  ]),
];
const seedValues = (
  action: OperationAffordance,
  declaration: OperationTaskDefinition,
): FormValues => {
  // Core owns the closed per-task seed map; this adapter only gives collection rows local identities.
  const seeds = action.seeds as Readonly<Record<string, FormValue | ReadonlyArray<FormValues>>>;
  const values: Record<string, FormValue> = {};
  for (const input of declaration.inputs) {
    if (Object.hasOwn(seeds, input.id)) {
      // oxlint-disable-next-line typescript/no-unsafe-type-assertion -- Scalar declarations select scalar Core seeds.
      values[input.id] = seeds[input.id] as FormValue;
    }
  }
  for (const collection of declaration.collections) {
    // oxlint-disable-next-line typescript/no-unsafe-type-assertion -- Core collection seeds are closed row maps.
    const rows = seeds[collection.input] as ReadonlyArray<FormValues> | undefined;
    values[collection.id] = {
      kind: "COLLECTION",
      rows: (rows ?? Array.from({ length: collection.minimumRows }, () => ({}))).map(
        (rowValues) => ({ key: crypto.randomUUID(), values: rowValues }),
      ),
    };
  }
  return values;
};
/** Newly added rows reuse the Core prefill, never the user's already edited rows. */
export const initialCollectionRow = (
  action: OperationAffordance | undefined,
  collectionId: string,
): FormValues => {
  if (action === undefined) {
    return {};
  }
  const value = seedValues(action, operationTask(action.task))[collectionId];
  return typeof value === "object" &&
    value !== null &&
    "kind" in value &&
    value.kind === "COLLECTION"
    ? (value.rows[0]?.values ?? {})
    : {};
};
/** Acquire seeds without activating editable UPDATE inputs; those seeds are display hints. */
export const initialAffordanceSeeds = (action: OperationAffordance): FormValues => {
  const declaration = operationTask(action.task);
  const seeds = seedValues(action, declaration);
  const values: Record<string, FormValue> = {};
  for (const input of declaration.inputs) {
    if (input.mode !== "UPDATE" || !new Set<string>(action.editable).has(input.id)) {
      const value = seeds[input.id];
      if (value !== undefined) {
        values[input.id] = value;
      }
    }
  }
  for (const collection of declaration.collections) {
    values[collection.id] = seeds[collection.id]!;
  }
  return values;
};
export const initialAffordanceValues = (action: OperationAffordance): FormValues => {
  const values = { ...initialAffordanceSeeds(action) };
  for (const input of operationTask(action.task).inputs) {
    if (
      values[input.id] === undefined &&
      input.source === "AUTHORED" &&
      input.mode === "REQUIRED" &&
      input.atom === "TEXT" &&
      input.choices.length === 0
    ) {
      values[input.id] = "";
    }
  }
  return values;
};
export const affordanceSeed = (
  action: OperationAffordance | undefined,
  input: string,
): FormValue | undefined => {
  if (action === undefined) {
    return undefined;
  }
  return seedValues(action, operationTask(action.task))[input];
};
