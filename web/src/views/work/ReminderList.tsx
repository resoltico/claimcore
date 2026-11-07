import { casePath } from "../../routing/routes";
import { navigate } from "../../routing/navigation";
import { PageTables } from "../../ui/PageTables";
import "./reminder-list.css";
import { Button } from "../../ui/Button";
import { Menu, MenuItem, MenuTrigger } from "react-aria-components/Menu";
import { Popover } from "react-aria-components/ComboBox";
import { useCallback, useRef } from "react";
import type {
  CaseSelector,
  ReminderReadModel,
  ReminderPage,
  ReminderListRequest,
  ResponseByEndpoint,
  WorkDateBasis,
} from "@contracts/client";
import { useEndpointPage } from "../../hooks/useEndpointPage";
import { usePresentation } from "../../presentation/context";
import { NoticeView } from "../../presentation/Message";
import { renderCatalogKey } from "../../presentation/messages";
import { operationEntry } from "../../protocol/operationCatalog";

type ReminderActHandler = (row: ReminderReadModel, kind: string, originId?: string) => void;

type ObservedReminder = { item: ReminderReadModel; basis: WorkDateBasis };
type ObservedReminderPage = Omit<ReminderPage, "items"> & { items: ObservedReminder[] };
const select = (reply: ResponseByEndpoint["reminder.list"]): ObservedReminderPage | null => {
  if (reply.outcome.kind !== "SUCCEEDED") {
    return null;
  }
  const page = reply.outcome.data;
  // Each loaded row keeps its own server observation rather than the latest page basis.
  return { ...page, items: page.items.map((item) => ({ item, basis: page.basis })) };
};
const ReminderActions = ({
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
  const other = row.acts.filter((kind) => kind !== "COMPLETE");
  return (
    <>
      <div className="reminder-complete-slot">
        {onAct === undefined || !row.acts.includes("COMPLETE") ? null : (
          <Button
            id={`reminder-${row.reminder.reminderId}-COMPLETE`}
            variant="secondary"
            className="reminder-complete"
            aria-label={`${label("COMPLETE")} · ${row.reminder.action}`}
            onPress={() => {
              onAct(row, "COMPLETE", `reminder-${row.reminder.reminderId}-COMPLETE`);
            }}
          >
            <span className="reminder-circle" aria-hidden="true" />
          </Button>
        )}
      </div>
      <div className="reminder-overflow-slot">
        {onAct === undefined || other.length === 0 ? null : (
          <MenuTrigger>
            <Button
              variant="secondary"
              className="reminder-overflow"
              ref={overflow}
              aria-label={`${p.text("label.availableActs")} · ${row.reminder.action}`}
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
    </>
  );
};
export const ReminderList = ({
  target,
  filter,
  onAct,
  onDisclosureRefused,
}: {
  target?: CaseSelector;
  filter: Omit<ReminderListRequest, "target" | "cursor" | "limit">;
  onAct?: ReminderActHandler;
  onDisclosureRefused?: () => void;
}) => {
  const p = usePresentation();
  const input = useCallback(
    (cursor: string | null): ReminderListRequest => ({
      ...filter,
      ...(target === undefined ? {} : { target }),
      ...(cursor === null ? {} : { cursor }),
      limit: 50,
    }),
    [filter, target],
  );
  const result = useEndpointPage<"reminder.list", ObservedReminder, ObservedReminderPage>(
    "reminder.list",
    input,
    select,
    onDisclosureRefused,
  );
  return (
    <section className="reminder-list" aria-busy={result.loading}>
      {target === undefined ? <p>{p.text("ui.reminderQueueHint")}</p> : null}
      {result.message === null ? null : (
        <p role="alert">
          <NoticeView value={result.message} />
        </p>
      )}
      {result.loading ? <p role="status">{p.text("ui.loading")}</p> : null}
      {result.items.length === 0 && !result.loading && result.message === null ? (
        <p>{p.text("ui.noReminders")}</p>
      ) : null}
      <PageTables
        views={result.views}
        rowLink={(index) => {
          const row = result.items[index]?.item;
          if (target !== undefined || row === undefined) {
            return undefined;
          }
          const href = casePath(row.reminder.caseId);
          return {
            href,
            follow: () => {
              navigate(href);
            },
          };
        }}
        renderActions={(index) => {
          const row = result.items[index]?.item;
          return row === undefined ? null : <ReminderActions row={row} onAct={onAct} />;
        }}
      />
      {result.page === null || result.items.length > 0 ? null : (
        <details>
          <summary>{p.text("label.recordDetails")}</summary>
          <p className="observation-basis">
            {p.text("ui.workDateBasis", {
              date: p.date(result.page.basis.businessDate),
              zone: result.page.basis.timeZone,
            })}
          </p>
        </details>
      )}
      {result.cursor === null ? null : (
        <button
          type="button"
          disabled={result.loading}
          onClick={() => void result.load(result.cursor)}
        >
          {p.text("ui.moreReminders")}
        </button>
      )}
      {target === undefined ? (
        <button type="button" disabled={result.loading} onClick={() => void result.load(null)}>
          {p.text("ui.refreshWork")}
        </button>
      ) : null}
    </section>
  );
};
