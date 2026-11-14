import type { OperationTaskDefinition, OperationBinding } from "@contracts/client";
import { type OperationAffordance, initialAffordanceValues } from "./affordances";

import type { FormValues } from "../protocol/forms";
import { assembleTask } from "./taskForm";
/** Initial authored values come from the selected Core control. */
export const initialOperationValues = (action: OperationAffordance): FormValues =>
  initialAffordanceValues(action);
export const assembleOperation = (
  task: OperationTaskDefinition,
  values: FormValues,
  binding: OperationBinding,
  operationId: string,
) => assembleTask(task.id, values, binding, operationId);
