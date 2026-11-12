import type { HumanRejection, OperationTaskDefinition } from "@contracts/client";
import type { Notice } from "../protocol/notices";
import type { TaskSubmission } from "./taskForm";
import { projectTaskIssues } from "./taskIssues";

export type FieldErrors = Readonly<Record<string, Notice>>;
export const inputErrors = (
  rejection: HumanRejection | undefined,
  task: OperationTaskDefinition,
  submittedRows: TaskSubmission["submittedRows"],
  localTarget: string | null,
  notice: Notice,
): FieldErrors => {
  if (rejection?.kind === "INPUT") {
    return projectTaskIssues(rejection.issues, task, submittedRows).fields;
  }
  return localTarget === null ? {} : { [localTarget]: notice };
};
