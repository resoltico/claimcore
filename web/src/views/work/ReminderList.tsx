import { useCallback } from "react";
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
export const ReminderList = ({
  target,
  filter,
  onAct,
  onDisclosureRefused,
}: {
  target?: CaseSelector;
  filter: Omit<ReminderListRequest, "target" | "cursor" | "limit">;
  onAct?: (row: ReminderReadModel, kind: string) => void;
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
      <p>{p.text("ui.reminderQueueHint")}</p>
      {result.message === null ? null : (
        <p role="alert">
          <NoticeView value={result.message} />
        </p>
      )}
      {result.loading ? <p role="status">{p.text("ui.loading")}</p> : null}
      {result.items.length === 0 && !result.loading && result.message === null ? (
        <p>{p.text("ui.noReminders")}</p>
      ) : null}
      <p className="observation-basis">{p.text("ui.liveWorkPages")}</p>
      <ol>
        {result.items.map(({ item: row, basis }) => (
          <li key={`${row.reminder.reminderId}:${row.reminder.revision}`}>
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
            <p className="reminder-action">
              <bdi dir="auto">{row.reminder.action}</bdi>
              <CharacterWarning value={row.reminder.action} />
            </p>
            <p>
              <bdi dir="ltr">{p.date(row.reminder.dueDate)}</bdi> ·{" "}
              {p.term("reminderState", row.reminder.state)}
              {row.timing === null ? null : (
                <>
                  {" "}
                  · <span className="reminder-timing">{p.term("reminderDue", row.timing)}</span>
                </>
              )}
            </p>
            <p className="observation-basis">
              {p.text("ui.workDateBasis", {
                date: p.date(basis.businessDate),
                zone: basis.timeZone,
              })}
            </p>
            {row.active ? null : <p>{p.text("ui.caseWorkInactive")}</p>}
            <div className="actions">
              {onAct === undefined
                ? null
                : (["COMPLETE_AND_PLAN_NEXT", "COMPLETE", "UPDATE", "CANCEL", "REOPEN"] as const)
                    .filter((kind) => row.acts.includes(kind))
                    .map((kind) => (
                      <button
                        id={`reminder-${row.reminder.reminderId}-${kind}`}
                        key={kind}
                        type="button"
                        onClick={() => {
                          onAct(row, kind);
                        }}
                      >
                        {renderCatalogKey(
                          p,
                          operationEntry({ subject: "REMINDER", kind: `REMINDER_${kind}` }).labelId,
                        )}
                      </button>
                    ))}
            </div>
            <details>
              <summary>{p.text("ui.reminderDetails")}</summary>
              <p>{p.text("ui.reminderObserved")}</p>
              <p>{p.text("ui.reminderRevision", { revision: p.integer(row.reminder.revision) })}</p>
              <IdentifierValue value={row.reminder.reminderId} />{" "}
              <CopyValue label={p.text("ui.reminderDetails")} value={row.reminder.reminderId} />
              <p>
                {p.text("ui.markedAt")}: <bdi dir="ltr">{row.recordedAt}</bdi>
              </p>
              <p>
                {p.text("ui.markedBy")}: <IdentifierValue value={row.recordedBy} />
              </p>
            </details>
          </li>
        ))}
      </ol>
      {result.page === null || result.items.length > 0 ? null : (
        <p className="observation-basis">
          {p.text("ui.workDateBasis", {
            date: p.date(result.page.basis.businessDate),
            zone: result.page.basis.timeZone,
          })}
        </p>
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
      <button type="button" disabled={result.loading} onClick={() => void result.load(null)}>
        {p.text("ui.refreshWork")}
      </button>
    </section>
  );
};
