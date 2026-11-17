import {
  type Affordance,
  type HumanDocument,
  type OperationTaskId,
  type OperationTaskDefinition,
  type OperationSubmission,
  taskDefinitions,
} from "@contracts/client";

import { operationTask } from "../protocol/taskCatalog";
import type { FormValue, FormValues } from "../protocol/forms";
export type OperationAffordance = Affordance & {
  readonly task: OperationTaskId;
  readonly binding: OperationSubmission["binding"];
};
export type BoundOperationAffordance<K extends OperationSubmission["binding"]["kind"]> =
  OperationAffordance & {
    readonly binding: Extract<OperationSubmission["binding"], { kind: K }>;
  };
export const isOperationAffordance = (action: Affordance): action is OperationAffordance =>
  taskDefinitions.some((task) => task.id === action.task && task.kind === "OPERATION") &&
  "kind" in action.binding;
export const hasOperationBinding = <K extends OperationSubmission["binding"]["kind"]>(
  action: Affordance,
  kind: K,
): action is BoundOperationAffordance<K> =>
  isOperationAffordance(action) && action.binding.kind === kind;
export const operationActions = (actions: ReadonlyArray<Affordance>) =>
  actions.filter(isOperationAffordance);
export const documentActions = (document: HumanDocument): ReadonlyArray<Affordance> => [
  ...document.screen.regions.flatMap((region) => region.actions ?? []),
  ...document.actions,
];
export const allActions = (document: HumanDocument): ReadonlyArray<Affordance> => [
  ...documentActions(document),
  ...document.screen.regions.flatMap((region) => [
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
  const values: Record<string, FormValue> = { ...action.seeds };
  for (const collection of declaration.collections) {
    values[collection.id] = {
      kind: "COLLECTION",
      rows: Array.from({ length: collection.minimumRows }, () => ({
        key: crypto.randomUUID(),
        values: { ...collection.defaults },
      })),
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
  const collection = operationTask(action.task).collections.find(
    (candidate) => candidate.id === collectionId,
  );
  return collection === undefined ? {} : { ...collection.defaults };
};
/** Acquire seeds without activating editable UPDATE inputs; those seeds are display hints. */
export const initialAffordanceSeeds = (action: OperationAffordance): FormValues => {
  const declaration = operationTask(action.task);
  const seeds = seedValues(action, declaration);
  const values: Record<string, FormValue> = {};
  for (const input of declaration.inputs) {
    if (input.fixedSeed) {
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
