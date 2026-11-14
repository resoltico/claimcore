import { taskForAct } from "./taskCatalog";
import type { OperationTaskDefinition } from "@contracts/client";

export type OperationSelection = {
  readonly subject: OperationTaskDefinition["subject"];
  readonly kind: string;
};
export const operationEntry = (identity: OperationSelection): OperationTaskDefinition =>
  taskForAct(identity.subject, identity.kind);
