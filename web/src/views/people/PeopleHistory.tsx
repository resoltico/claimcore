import type { RegionTimelineEntry } from "@contracts/client";

import { PageTables } from "../../ui/PageTables";
import { useCallback } from "react";

type HistoryControl = RegionTimelineEntry;
import type { ServiceResponse } from "../../api/service";
import { useEndpointPage } from "../../hooks/useEndpointPage";
import { usePresentation } from "../../presentation/context";
import { NoticeView } from "../../presentation/Message";
const select = (reply: ServiceResponse<"people.history">) => {
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
  const page = useEndpointPage<"people.history", HistoryControl, HistoryPage>(
    "people.history",
    input,
    select,
  );
  return (
    <section className="people-history" aria-labelledby="people-history-title">
      <h1 id="people-history-title">{p.text("endpoint.people.history.label")}</h1>
      {page.loading ? <p role="status">{p.text("ui.loadingHistory")}</p> : null}
      {page.message === null ? null : (
        <p role="alert">
          <NoticeView value={page.message} />
        </p>
      )}
      <h2 className="sr-only">{p.text("ui.timeline.title")}</h2>
      <PageTables views={page.views} />

      <button
        type="button"
        disabled={page.loading}
        onClick={() => {
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
            void page.load(page.cursor);
          }}
        >
          {p.text("ui.moreHistory")}
        </button>
      )}
    </section>
  );
};
