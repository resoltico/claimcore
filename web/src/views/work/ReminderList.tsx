import { apiEndpoints } from "../../api/service";
import { isOperationAffordance } from "../../operation/affordances";
import type {
  RegionRow,
  CaseSelector,
  ReminderListRequest,
  HumanResponseByEndpoint,
} from "@contracts/client";
import { reminderActionPresentation, type ReminderActHandler } from "./reminderActions";

import { ListFooter } from "../../ui/ListFooter";
import { ResultPages } from "../../ui/ResultPages";
import {} from "./reminder-list.css";
import { useCallback } from "react";

import { useEndpointPage } from "../../hooks/useEndpointPage";
import { usePresentation } from "../../presentation/context";
import { NoticeView } from "../../presentation/Message";

const select = (reply: HumanResponseByEndpoint[typeof apiEndpoints.reminderList]) => {
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
  const result = useEndpointPage<typeof apiEndpoints.reminderList, RegionRow, ObservedReminderPage>(
    apiEndpoints.reminderList,
    input,
    select,
    onDisclosureRefused,
  );
  return (
    <section className="reminder-list" aria-busy={result.loading}>
      {target === undefined ? <p>{p.text("label.reminderQueueHint")}</p> : null}
      {result.message === null ? null : (
        <p role="alert">
          <NoticeView value={result.message} />
        </p>
      )}
      {result.loading ? <p role="status">{p.text("ui.loading")}</p> : null}
      {result.items.length === 0 && !result.loading && result.message === null ? (
        <p>{p.text("label.noReminders")}</p>
      ) : null}
      <ResultPages
        showTableSections={false}
        views={result.views}
        actionPresentation={reminderActionPresentation}
        {...(onAct === undefined
          ? {}
          : {
              onAction: (action, _row, originId, implicit) => {
                if (isOperationAffordance(action)) {
                  onAct(action, originId, implicit);
                }
              },
            })}
      />
      {result.page === null ? null : (
        <ListFooter
          subject="reminders"
          count={result.items.length}
          basis={result.page.basis}
          continuation={result.cursor}
          loading={result.loading}
          load={(cursor) => {
            void result.load(cursor);
          }}
          busy={result.refreshBusy}
          refresh={result.refresh}
        />
      )}
    </section>
  );
};
