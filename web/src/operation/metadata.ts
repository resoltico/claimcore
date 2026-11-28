import { operationTask } from "../protocol/taskCatalog";
import {
  clientCompositions,
  taskDefinitions,
  type OperationTaskDefinition,
} from "@contracts/client";
export type ActKind = Extract<OperationTaskDefinition, { subject: "CASE" | "PARTY" }>["id"];
export const isCaseAct = (value: string): value is ActKind =>
  taskDefinitions.some(
    (task) =>
      task.kind === "OPERATION" &&
      (task.subject === "CASE" || task.subject === "PARTY") &&
      task.id === value,
  );

/** The people coordinator starts inert; only a current offered control enables authoring. */
export const principalCreationTask = (): OperationTaskDefinition =>
  operationTask(clientCompositions.admission);

/** Presentation attaches the note entry composition explicitly. */
export const narrativeCreationInput = (task: OperationTaskDefinition) =>
  task.id === clientCompositions.noteEntry
    ? task.inputs.find((input) => input.source === "AUTHORED")
    : undefined;
export const isNarrativeCreationId = (id: string) =>
  taskDefinitions.some(
    (task) =>
      task.kind === "OPERATION" && task.id === id && narrativeCreationInput(task) !== undefined,
  );
