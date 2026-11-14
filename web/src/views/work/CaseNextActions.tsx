import type { taskChoices, CaseSelector, ReminderListRequest } from "@contracts/client";
import { queryInput } from "../../protocol/taskCatalog";
import { isTerm } from "../../presentation/terms";
import type { CurrentCase } from "../../protocol/types";
import { usePresentation } from "../../presentation/context";
import { renderCatalogKey } from "../../presentation/messages";
import { Table } from "../../ui/regions/Table";
import { Select } from "../../ui/Select";
import { reminderActionPresentation, type ReminderActHandler } from "./reminderActions";
import { ReminderList } from "./ReminderList";
const stateChoice = queryInput("reminder.list", "input:reminderState");
type State = (typeof taskChoices.reminderState)[number];
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
  onReminder: ReminderActHandler;
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
  const nextRegion = current.screen.regions.find((region) => region.role === "NEXT_ACTIONS");
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
            {p.text("task.reminder.create.label")}
          </button>
        ) : null}
      </div>
      {!browsing && nextRegion.content.rows.length === 0 ? <p>{p.text("view.empty")}</p> : null}
      {browsing ? null : (
        <Table
          view={nextRegion.content}
          actionPresentation={reminderActionPresentation}
          timeZone={current.basis.observation.timeZone}
          onAction={(action, _row, originId) => {
            if (action.task !== "people.requests.dismiss") {
              onReminder(action, originId);
            }
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
          options={stateChoice.choices
            .filter((value) => isTerm("reminderState", value))
            .map((value) => ({
              value,
              label: p.term("reminderState", value),
            }))}
          onChange={(value) => {
            if (isTerm("reminderState", value) && stateChoice.choices.includes(value)) {
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
