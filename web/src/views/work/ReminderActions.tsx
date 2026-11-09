import { useRef } from "react";
import {
  taskDefinitions,
  type HumanReadDocument_reminderList,
  type OperationTaskId,
} from "@contracts/client";
import { Button } from "../../ui/Button";
import { Menu, MenuItem, MenuTrigger } from "react-aria-components/Menu";
import { Popover } from "react-aria-components/ComboBox";
import { operationTask } from "../../protocol/taskCatalog";
import { usePresentation } from "../../presentation/context";
import { renderCatalogKey } from "../../presentation/messages";

export type ReminderControl = HumanReadDocument_reminderList["context"]["reminders"][number];
export type ReminderActHandler = (
  row: ReminderControl,
  task: OperationTaskId,
  originId?: string,
) => void;
export const ReminderActions = ({
  row,
  onAct,
}: {
  row: ReminderControl;
  onAct: ReminderActHandler | undefined;
}) => {
  const p = usePresentation();
  const overflow = useRef<HTMLButtonElement>(null);
  const tasks = row.tasks.filter((id): id is OperationTaskId => operationTaskKind(id));
  const complete = tasks.find((id) => id === "REMINDER:REMINDER_COMPLETE");
  const update = tasks.find((id) => id === "REMINDER:REMINDER_UPDATE");
  const other = tasks.filter((id) => id !== complete && id !== update);
  const label = (id: OperationTaskId) => renderCatalogKey(p, operationTask(id).labelId);
  const primary = (id: OperationTaskId | undefined, className?: string) =>
    id === undefined || onAct === undefined ? null : (
      <Button
        id={"reminder-" + row.reminderId + "-" + id}
        variant="secondary"
        {...(className === undefined ? {} : { className })}
        onPress={() => onAct(row, id, "reminder-" + row.reminderId + "-" + id)}
      >
        {label(id)}
      </Button>
    );
  return (
    <div className="reminder-row-actions">
      <div className="reminder-complete-slot">{primary(complete, "reminder-complete")}</div>
      {primary(update)}
      <div className="reminder-overflow-slot">
        {onAct === undefined || other.length === 0 ? null : (
          <MenuTrigger>
            <Button
              variant="secondary"
              className="reminder-overflow"
              ref={overflow}
              aria-label={p.text("label.availableActs")}
            >
              <span aria-hidden="true">⋯</span>
            </Button>
            <Popover className="reminder-actions-popover" placement="bottom end" isNonModal>
              <Menu className="reminder-actions-menu" aria-label={p.text("label.availableActs")}>
                {other.map((id) => (
                  <MenuItem
                    key={id}
                    id={"reminder-" + row.reminderId + "-" + id}
                    textValue={label(id)}
                    className="reminder-actions-item"
                    onAction={() => onAct(row, id, overflow.current?.id)}
                  >
                    {label(id)}
                  </MenuItem>
                ))}
              </Menu>
            </Popover>
          </MenuTrigger>
        )}
      </div>
    </div>
  );
};
const operationTaskKind = (id: string): id is OperationTaskId =>
  reminderTaskIds.some((task) => task === id);
const reminderTaskIds = taskDefinitions
  .filter((task) => task.kind === "OPERATION" && task.subject === "REMINDER")
  .map((task) => task.id);
