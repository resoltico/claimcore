import type {
  RegionRow,
  CaseSelector,
  ReminderListRequest,
  HumanResponseByEndpoint,
} from "@contracts/client";
import type { ReminderActHandler } from "./reminderActions";

import { PageTables } from "../../ui/PageTables";
import {} from "./reminder-list.css";
import { useCallback } from "react";

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
    items: doc.screen.regions.flatMap((region) =>
      region.content.kind === "TABLE" ? region.content.rows : [],
    ),
    nextCursor: doc.nextCursor,
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
  const result = useEndpointPage<"reminder.list", RegionRow, ObservedReminderPage>(
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
        {...(onAct === undefined
          ? {}
          : {
              onAction: (action, _row, originId) => {
                if (action.task !== "people.requests.dismiss") {
                  onAct(action, originId);
                }
              },
            })}
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
