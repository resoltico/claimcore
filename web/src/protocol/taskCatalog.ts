import {
  taskDefinitions,
  taskInputDefinitions,
  type OperationTaskId,
  type OperationTaskDefinition,
  type TaskInputId,
  type TaskInputDefinition,
} from "@contracts/client";

export const operationTask = (id: OperationTaskId): OperationTaskDefinition => {
  const task = taskDefinitions.find(
    (task): task is OperationTaskDefinition => task.kind === "OPERATION" && task.id === id,
  );
  if (task === undefined) {
    throw new Error("Unknown operation task.");
  }
  return task;
};

export const taskForAct = (
  subject: OperationTaskDefinition["subject"],
  act: string,
): OperationTaskDefinition => {
  const task = taskDefinitions.find(
    (task): task is OperationTaskDefinition =>
      task.kind === "OPERATION" && task.subject === subject && task.act === act,
  );
  if (task === undefined) {
    throw new Error("Unknown operation action.");
  }
  return task;
};

export const taskForCommand = (input: {
  readonly kind: string;
  readonly act: { readonly kind: string };
}): OperationTaskDefinition => {
  const task = taskDefinitions.find(
    (task): task is OperationTaskDefinition =>
      task.kind === "OPERATION" && task.subject === input.kind && task.wireAct === input.act.kind,
  );
  if (task === undefined) {
    throw new Error("Unknown operation command.");
  }
  return task;
};

export const taskInput = (id: TaskInputId): TaskInputDefinition => {
  const input = taskInputDefinitions.find((input) => input.id === id);
  if (input === undefined) {
    throw new Error("Unknown task input.");
  }
  return input;
};
