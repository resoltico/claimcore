import "./reminder-list.css";
import { Button } from "../../ui/Button";
import { Menu, MenuItem, MenuTrigger } from "react-aria-components/Menu";
import { Popover } from "react-aria-components/ComboBox";
import { ActorIdentity } from "../../components/ActorIdentity";
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
import { CharacterWarning } from "../../components/BusinessValue";
import { CopyValue } from "../../components/CopyValue";
import { IdentifierValue } from "../../components/IdentifierValue";
import { casePath } from "../../routing/routes";
import { navigate } from "../../routing/navigation";
import { followLink } from "../../routing/followLink";
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
            aria-label={label("COMPLETE")}
            aria-describedby={`reminder-action-${row.reminder.reminderId}`}
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
              aria-label={p.text("label.availableActs")}
              aria-describedby={`reminder-action-${row.reminder.reminderId}`}
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
const ReminderRow = ({
  row,
  basis,
  target,
  onAct,
}: {
  row: ReminderReadModel;
  basis: WorkDateBasis;
  target: CaseSelector | undefined;
  onAct: ReminderActHandler | undefined;
}) => {
  const p = usePresentation();
  return (
    <li
      className="reminder-row"
      data-muted={!row.active || row.reminder.state !== "PENDING" || undefined}
    >
      <ReminderActions row={row} onAct={onAct} />
      <div className="reminder-content">
        <p className="reminder-action" id={`reminder-action-${row.reminder.reminderId}`}>
          <bdi dir="auto">{row.reminder.action}</bdi>
          <CharacterWarning value={row.reminder.action} />
        </p>
        <div className="reminder-meta">
          <time className="reminder-due" dateTime={row.reminder.dueDate}>
            <bdi dir="ltr">{p.date(row.reminder.dueDate)}</bdi>
          </time>
          {row.timing === null ? null : (
            <span className="reminder-timing" data-timing={row.timing}>
              {p.term("reminderDue", row.timing)}
            </span>
          )}
          <span className="reminder-state">{p.term("reminderState", row.reminder.state)}</span>
          {target === undefined ? (
            <a
              href={casePath(row.reminder.caseId)}
              onClick={(event) => {
                followLink(event, () => {
                  navigate(casePath(row.reminder.caseId));
                });
              }}
            >
              <bdi dir="ltr">{row.caseReference}</bdi>
            </a>
          ) : null}
        </div>
        <details className="reminder-record-details">
          <summary>{p.text("ui.reminderDetails")}</summary>
          <p>{p.text("ui.reminderObserved")}</p>
          <p>{p.text("ui.reminderRevision", { revision: p.integer(row.reminder.revision) })}</p>
          <IdentifierValue value={row.reminder.reminderId} />{" "}
          <CopyValue label={p.text("ui.reminderDetails")} value={row.reminder.reminderId} />
          <p>
            {p.text("ui.markedAt")}: <bdi dir="ltr">{row.recordedAt}</bdi>
          </p>
          <p>
            {p.text("ui.markedBy")}: <ActorIdentity actor={row.recordedBy} />
          </p>
          <p className="observation-basis">
            {p.text("ui.workDateBasis", { date: p.date(basis.businessDate), zone: basis.timeZone })}
          </p>
          {row.active ? null : <p>{p.text("ui.caseWorkInactive")}</p>}
        </details>
      </div>
    </li>
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
      <ol className="reminder-list-items">
        {result.items.map(({ item: row, basis }) => (
          <ReminderRow
            key={`${row.reminder.reminderId}:${row.reminder.revision}`}
            row={row}
            basis={basis}
            target={target}
            onAct={onAct}
          />
        ))}
      </ol>
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
