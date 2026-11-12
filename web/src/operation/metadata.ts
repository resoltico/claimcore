import type { CaseAct } from "@contracts/client";
import { taskDefinitions } from "@contracts/client";
export type ActKind = CaseAct["kind"];
export const isCaseAct = (value: string): value is ActKind =>
  taskDefinitions.some(
    (task) => task.kind === "OPERATION" && task.subject === "CASE" && task.act === value,
  );
