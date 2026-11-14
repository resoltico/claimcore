import { taskDefinitions, type OperationTaskDefinition } from "@contracts/client";
export type ActKind = Extract<OperationTaskDefinition, { subject: "CASE" }>["act"];
export const isCaseAct = (value: string): value is ActKind =>
  taskDefinitions.some(
    (task) => task.kind === "OPERATION" && task.subject === "CASE" && task.act === value,
  );
