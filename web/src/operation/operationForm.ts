import type { OperationTaskDefinition } from "@contracts/client";
import { operationEntry, type OperationSelection } from "../protocol/operationCatalog";
import { taskInput } from "../protocol/taskCatalog";
import type { FormValues, FormValue } from "../protocol/forms";
import type { CurrentCase } from "../protocol/types";
import { assembleTask, type TaskBinding } from "./taskForm";

/** Draft acquisition has no executable identity or version; those belong to its reviewed binding. */
export const initialOperationValues = (
  subject: OperationSelection["subject"],
  kind: string,
  current: CurrentCase | null,
): FormValues => {
  const task = operationEntry({ subject, kind });
  const values: Record<string, FormValue> = {};
  for (const input of task.inputs) {
    if (input.source !== "AUTHORED" || input.mode !== "REQUIRED") {
      continue;
    }
    if (input.id === "field:caseReference" && current !== null && subject !== "AUTHORITY") {
      continue;
    }
    if (
      input.id === "input:noteId" ||
      input.id === "input:reminderId" ||
      input.id === "input:paymentId"
    ) {
      continue;
    }
    const metadata = taskInput(input.id);
    if (metadata.kind === "GROUP") {
      values[input.id] = "KEEP";
      continue;
    }
    if (input.id === "input:scope") {
      values[input.id] = "INSTALLATION";
      continue;
    }
    if (input.id === "input:principal.kind") {
      values[input.id] = "HUMAN";
      continue;
    }
    const seed =
      metadata.group === "registration"
        ? current?.context.seeds.registration
        : metadata.group === "decision"
          ? current?.context.seeds.decision
          : null;
    const existing =
      seed === null || seed === undefined
        ? undefined
        : Object.entries(seed).find(([key]) => key === metadata.key)?.[1];
    values[input.id] = existing ?? "";
  }
  for (const collection of task.collections) {
    values[collection.id] = {
      kind: "COLLECTION",
      rows: Array.from({ length: collection.minimumRows }, () => ({
        key: crypto.randomUUID(),
        values: { "input:scope": "INSTALLATION" },
      })),
    };
  }
  return values;
};
export const assembleOperation = (
  task: OperationTaskDefinition,
  values: FormValues,
  binding: TaskBinding,
) => assembleTask(task.id, values, binding);
