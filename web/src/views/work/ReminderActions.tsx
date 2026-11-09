import { useRef } from "react";
import type { ReminderReadModel } from "@contracts/client";
import { Button } from "../../ui/Button";
import { Menu, MenuItem, MenuTrigger } from "react-aria-components/Menu";
import { Popover } from "react-aria-components/ComboBox";
import { operationEntry } from "../../protocol/operationCatalog";
import { usePresentation } from "../../presentation/context";
import { renderCatalogKey } from "../../presentation/messages";
export type ReminderActHandler = (row: ReminderReadModel, kind: string, originId?: string) => void;

export const ReminderActions = ({
  row,
  onAct,
}: {
  row: ReminderReadModel;
  onAct: ReminderActHandler | undefined;
}) => {
  const p = usePresentation();
  const label = (kind: ReminderReadModel["acts"][number]) =>
    renderCatalogKey(p, operationEntry({ subject: "REMINDER", kind: `REMINDER_${kind}` }).labelId);
  const overflow = useRef<HTMLButtonElement>(null);
  const other = row.acts.filter((kind) => kind !== "COMPLETE" && kind !== "UPDATE");
  return (
    <div className="reminder-row-actions">
      <div className="reminder-complete-slot">
        {onAct === undefined || !row.acts.includes("COMPLETE") ? null : (
          <Button
            id={`reminder-${row.reminder.reminderId}-COMPLETE`}
            variant="secondary"
            className="reminder-complete"
            aria-label={`${p.text("ui.complete")}: ${label("COMPLETE")}`}
            onPress={() => {
              onAct(row, "COMPLETE", `reminder-${row.reminder.reminderId}-COMPLETE`);
            }}
          >
            {p.text("ui.complete")}
          </Button>
        )}
      </div>
      {onAct === undefined || !row.acts.includes("UPDATE") ? null : (
        <Button
          id={`reminder-${row.reminder.reminderId}-UPDATE`}
          aria-label={`${p.text("ui.reschedule")}: ${label("UPDATE")}`}
          variant="secondary"
          onPress={() => {
            onAct(row, "UPDATE", `reminder-${row.reminder.reminderId}-UPDATE`);
          }}
        >
          {p.text("ui.reschedule")}
        </Button>
      )}
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
                {other.map((kind) => (
                  <MenuItem
                    id={`reminder-${row.reminder.reminderId}-${kind}`}
                    key={kind}
                    textValue={label(kind)}
                    className="reminder-actions-item"
                    onAction={() => {
                      onAct(row, kind, overflow.current?.id);
                    }}
                  >
                    {label(kind)}
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
