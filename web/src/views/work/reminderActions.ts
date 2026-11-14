import type { Affordance } from "@contracts/client";
import type { OperationAffordance } from "../../operation/affordances";
export type ReminderActHandler = (action: OperationAffordance, originId?: string) => void;

export const reminderActionPresentation = (action: Affordance) => ({
  ...(action.binding.kind === "REMINDER" && "reminderId" in action.binding
    ? { id: `reminder-${action.binding.reminderId}-${action.task}` }
    : {}),
});
