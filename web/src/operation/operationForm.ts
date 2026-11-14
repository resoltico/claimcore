import { type OperationTaskDefinition, type OperationBinding } from "@contracts/client";
import { operationEntry, type OperationSelection } from "../protocol/operationCatalog";
import { taskInput } from "../protocol/taskCatalog";
import type { FormValues, FormValue } from "../protocol/forms";
import type { CurrentCase } from "../protocol/types";
import { assembleTask } from "./taskForm";

const groupSeed = (current: CurrentCase | null, group: ReturnType<typeof taskInput>["group"]) => {
  switch (group) {
    case "registration":
      return current?.context.seeds.registration ?? null;
    case "decision":
      return current?.context.seeds.decision ?? null;
    default:
      return null;
  }
};
const requiredInputValue = (
  input: OperationTaskDefinition["inputs"][number],
  current: CurrentCase | null,
): FormValue => {
  const metadata = taskInput(input.id);
  if (metadata.kind === "GROUP") {
    return "KEEP";
  }
  if (input.id === "input:scope") {
    return "INSTALLATION";
  }
  if (input.id === "input:principal.kind") {
    return "HUMAN";
  }
  if (input.id === "field:paidCurrency") {
    return current?.context.seeds.paymentCurrency ?? "";
  }
  const seed = groupSeed(current, metadata.group);
  return seed === null
    ? ""
    : (Object.entries(seed).find(([key]) => key === metadata.key)?.[1] ?? "");
};
const acquiredInput = (
  input: OperationTaskDefinition["inputs"][number],
  subject: OperationSelection["subject"],
  current: CurrentCase | null,
) =>
  (input.source === "AUTHORED" ||
    (input.id === "field:caseReference" && current === null && subject === "CASE")) &&
  input.mode === "REQUIRED" &&
  !["input:noteId", "input:reminderId", "input:paymentId"].includes(input.id);

/** Draft acquisition has no executable identity or version; those belong to its reviewed binding. */
export const initialOperationValues = (
  subject: OperationSelection["subject"],
  kind: string,
  current: CurrentCase | null,
): FormValues => {
  const task = operationEntry({ subject, kind });
  const values: Record<string, FormValue> = {};
  for (const input of task.inputs) {
    if (acquiredInput(input, subject, current)) {
      values[input.id] = requiredInputValue(input, current);
    }
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
  binding: OperationBinding,
  operationId: string,
) => assembleTask(task.id, values, binding, operationId);
