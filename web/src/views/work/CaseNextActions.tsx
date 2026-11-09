import { queryChoice } from "../../presentation/queryChoices";
import { isTerm } from "../../presentation/terms";
import type { CurrentCase } from "../../protocol/types";
import type { ReminderReadModel, CaseSelector, ReminderListRequest } from "@contracts/client";
import { usePresentation } from "../../presentation/context";
import { renderCatalogKey } from "../../presentation/messages";
import { Table } from "../../ui/regions/Table";
import { Select } from "../../ui/Select";
import { ReminderActions } from "./ReminderActions";
import { ReminderList } from "./ReminderList";
const stateChoice = queryChoice("reminder.list", "reminderState");
type State = ReminderReadModel["reminder"]["state"];
export const CaseNextActions = ({
  current,
  canCreate,
  onCreate,
  onReminder,
  state,
  setState,
  browsing,
  setBrowsing,
  refresh,
  target,
  filter,
  onChanged,
  onRefresh,
}: {
  current: CurrentCase;
  canCreate: boolean;
  onCreate: () => void;
  onReminder: (row: ReminderReadModel, kind: string, origin?: string) => void;
  state: State;
  setState: (state: State) => void;
  browsing: boolean;
  setBrowsing: (value: boolean) => void;
  refresh: number;
  target: CaseSelector;
  filter: Omit<ReminderListRequest, "target" | "cursor" | "limit">;
  onChanged: () => void;
  onRefresh: () => void;
}) => {
  const p = usePresentation();
  const nextRegion = current.view.regions.find((region) => region.role === "NEXT_ACTIONS");
  if (nextRegion?.content.kind !== "TABLE") {
    return null;
  }
  return (
    <section
      className="case-reminders screen-region"
      data-region="NEXT_ACTIONS"
      aria-labelledby="case-reminders-title"
    >
      <div className="region-heading">
        <h2 id="case-reminders-title">
          {renderCatalogKey(p, nextRegion.titleId ?? "label.nextActions")}
        </h2>
        {canCreate ? (
          <button id="case-reminder-add" type="button" onClick={onCreate}>
            {p.text("ui.newReminder")}
          </button>
        ) : null}
      </div>
      {!browsing && nextRegion.content.rows.length === 0 ? <p>{p.text("view.empty")}</p> : null}
      {browsing ? null : (
        <Table
          view={nextRegion.content}
          timeZone={current.work.basis.timeZone}
          renderActions={(key) => {
            const row = current.nextActions.find((item) => item.reminder.reminderId === key);
            return row === undefined ? null : <ReminderActions row={row} onAct={onReminder} />;
          }}
        />
      )}
      <details
        className="case-reminder-controls"
        onToggle={(event) => {
          setBrowsing(event.currentTarget.open);
        }}
      >
        <summary>
          {p.text("input.reminderState.label")} · {p.term("reminderState", state)}
        </summary>
        <Select
          label={renderCatalogKey(p, stateChoice.labelId)}
          value={state}
          options={stateChoice.options.map((value) => ({
            value,
            label: p.term("reminderState", value),
          }))}
          onChange={(value) => {
            if (isTerm("reminderState", value) && stateChoice.options.includes(value)) {
              setState(value);
            }
          }}
        />
        {browsing ? (
          <ReminderList
            key={refresh}
            target={target}
            filter={filter}
            onDisclosureRefused={onChanged}
            onAct={onReminder}
          />
        ) : null}
        <button type="button" onClick={onRefresh}>
          {p.text("ui.refreshWork")}
        </button>
      </details>
    </section>
  );
};
