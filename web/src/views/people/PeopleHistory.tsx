import { CsvExportButton } from "../../components/CsvExportButton";
import { apiEndpoints, type ServiceResponse } from "../../api/service";
import type { RegionTimelineEntry } from "@contracts/client";

import { PageTables } from "../../ui/PageTables";
import { useCallback } from "react";

type HistoryControl = RegionTimelineEntry;

import { useEndpointPage } from "../../hooks/useEndpointPage";
import { usePresentation } from "../../presentation/context";
import { NoticeView } from "../../presentation/Message";
const select = (reply: ServiceResponse<typeof apiEndpoints.peopleHistory>) => {
  if (reply.outcome.kind !== "SUCCEEDED") {
    return null;
  }
  const doc = reply.outcome.document;
  if (doc.basis.kind !== "SNAPSHOT") {
    return null;
  }
  return {
    items: doc.screen.regions.flatMap((region) =>
      region.content.kind === "TIMELINE" ? region.content.entries : [],
    ),
    nextCursor: doc.nextCursor,
    basis: doc.basis.observation,
    screen: doc.screen,
    disclosure: doc.disclosure,
  };
};
type HistoryPage = NonNullable<ReturnType<typeof select>>;
export const PeopleHistory = ({ actorId }: { actorId: string }) => {
  const p = usePresentation();
  const input = useCallback(
    (cursor: string | null) => ({
      limit: 50,
      ...(actorId === "" ? {} : { actorId }),
      ...(cursor === null ? {} : { cursor }),
    }),
    [actorId],
  );
  const page = useEndpointPage<typeof apiEndpoints.peopleHistory, HistoryControl, HistoryPage>(
    apiEndpoints.peopleHistory,
    input,
    select,
  );
  return (
    <section className="people-history" aria-labelledby="people-history-title">
      <h1 id="people-history-title" tabIndex={-1}>
        {p.text(actorId === "" ? "endpoint.people.history.label" : "ui.accountAccessHistory")}
      </h1>
      {page.loading ? <p role="status">{p.text("ui.loadingHistory")}</p> : null}
      {page.message === null ? null : (
        <p role="alert">
          <NoticeView value={page.message} />
        </p>
      )}
      <h2 className="sr-only">{p.text("ui.timeline.title")}</h2>
      <PageTables views={page.views} />

      <div className="worklist-actions" role="group" aria-label={p.text("label.peopleHistory")}>
        <CsvExportButton
          selection={{
            query: apiEndpoints.peopleHistory,
            selection: actorId === "" ? {} : { actorId },
          }}
          available={page.page !== null && !page.loading}
        />
        <button
          type="button"
          disabled={page.loading}
          onClick={() => {
            document.getElementById("people-history-title")?.focus({ preventScroll: true });
            void page.load(null);
          }}
        >
          {p.text("ui.reloadHistory")}
        </button>
        {page.cursor === null ? null : (
          <button
            type="button"
            disabled={page.loading}
            onClick={() => {
              document.getElementById("people-history-title")?.focus({ preventScroll: true });
              void page.load(page.cursor);
            }}
          >
            {p.text("ui.moreHistory")}
          </button>
        )}
      </div>
    </section>
  );
};
