import { ReminderActions, type ReminderActHandler } from "./ReminderActions";
import { casePath } from "../../routing/routes";
import { navigate } from "../../routing/navigation";
import { PageTables } from "../../ui/PageTables";
import "./reminder-list.css";
import { useCallback } from "react";
import type { CaseSelector, ReminderListRequest, HumanResponseByEndpoint } from "@contracts/client";
import type { ReminderControl } from "./ReminderActions";
import { useEndpointPage } from "../../hooks/useEndpointPage";
import { usePresentation } from "../../presentation/context";
import { NoticeView } from "../../presentation/Message";

const select = (reply: HumanResponseByEndpoint["reminder.list"]) => {
  if (reply.outcome.kind !== "SUCCEEDED") {
    return null;
  }
  const doc = reply.outcome.document;
  if (doc.basis.kind !== "SNAPSHOT") {
    return null;
  }
  return {
    items: doc.context.reminders,
    nextCursor: doc.context.nextCursor,
    basis: doc.basis.observation,
    screen: doc.screen,
    disclosure: doc.disclosure,
  };
};
type ObservedReminderPage = NonNullable<ReturnType<typeof select>>;
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
  const result = useEndpointPage<"reminder.list", ReminderControl, ObservedReminderPage>(
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
        rowLink={(key) => {
          const row = result.items.find((candidate) => candidate.reminderId === key);
          if (target !== undefined || row === undefined) {
            return undefined;
          }
          const href = casePath(row.caseId);
          return {
            column: "caseReference",
            href,
            follow: () => {
              navigate(href);
            },
          };
        }}
        renderActions={(key) => {
          const row = result.items.find((candidate) => candidate.reminderId === key);
          return row === undefined ? null : <ReminderActions row={row} onAct={onAct} />;
        }}
      />
      {result.cursor === null ? null : (
        <button
          type="button"
          disabled={result.loading}
          onClick={() => {
            void result.load(result.cursor);
          }}
        >
          {p.text("ui.moreReminders")}
        </button>
      )}
      {target === undefined ? (
        <button
          type="button"
          disabled={result.loading}
          onClick={() => {
            void result.load(null);
          }}
        >
          {p.text("ui.refreshWork")}
        </button>
      ) : null}
    </section>
  );
};
