import { taskScalar } from "../protocol/taskCatalog";
import { taskDefinitions, type OperationTaskDefinition } from "@contracts/client";
export type ActKind = Extract<OperationTaskDefinition, { subject: "CASE" }>["id"];
export const isCaseAct = (value: string): value is ActKind =>
  taskDefinitions.some(
    (task) => task.kind === "OPERATION" && task.subject === "CASE" && task.id === value,
  );

/** The people coordinator starts inert; only a current offered control enables authoring. */
export const principalCreationTask = (): OperationTaskDefinition => {
  const task = taskDefinitions.find(
    (candidate): candidate is OperationTaskDefinition =>
      candidate.kind === "OPERATION" &&
      candidate.subject === "PEOPLE" &&
      candidate.creates &&
      candidate.collections.length > 0,
  );
  if (task === undefined) {
    throw new Error("The principal creation declaration is missing.");
  }
  return task;
};

/** Inline narrative acquisition is selected from the declared form shape. */
export const narrativeCreationInput = (task: OperationTaskDefinition) => {
  const authored = task.inputs.filter((input) => input.source === "AUTHORED");
  if (!task.creates || authored.length !== 1) {
    return undefined;
  }
  const input = authored[0]!;
  const field = taskScalar(input.id);
  return field?.scalar?.kind === "PLAIN_TEXT" && field.scalar.allowsLineFeed ? input : undefined;
};
export const isNarrativeCreationId = (id: string) =>
  taskDefinitions.some(
    (task) =>
      task.kind === "OPERATION" && task.id === id && narrativeCreationInput(task) !== undefined,
  );
