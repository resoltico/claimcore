import { useDocumentTitle } from "../../hooks/useDocumentTitle";
import { Page } from "../../ui/Page";
import { DirectoryPersonPicker } from "../../components/AssigneeInput";
import { useCallback, useMemo } from "react";
import type { RegionTimelineEntry } from "@contracts/client";
import { CsvExportButton } from "../../components/CsvExportButton";
import { apiEndpoints, type ServiceResponse } from "../../api/service";
import { useEndpointPage } from "../../hooks/useEndpointPage";
import { usePresentation } from "../../presentation/context";
import { NoticeView } from "../../presentation/Message";
import { navigate } from "../../routing/navigation";
import { peopleHistoryPath } from "../../routing/routes";
import { ListFooter } from "../../ui/ListFooter";
import { ResultPages } from "../../ui/ResultPages";
import { Toolbar } from "../../ui/Toolbar";

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
  useDocumentTitle(
    p.text(actorId === "" ? "endpoint.people.history.label" : "ui.accountAccessHistory"),
  );
  const selection = useMemo(() => (actorId === "" ? {} : { actorId }), [actorId]);
  const input = useCallback(
    (cursor: string | null) => ({
      limit: 50,
      ...selection,
      ...(cursor === null ? {} : { cursor }),
    }),
    [selection],
  );
  const page = useEndpointPage<typeof apiEndpoints.peopleHistory, RegionTimelineEntry, HistoryPage>(
    apiEndpoints.peopleHistory,
    input,
    select,
  );
  return (
    <Page
      className="people-history"
      id="people-history-title"
      title={p.text(actorId === "" ? "endpoint.people.history.label" : "ui.accountAccessHistory")}
    >
      <Toolbar
        filter={
          <DirectoryPersonPicker
            selected={actorId}
            change={(value) => {
              if (value !== actorId) {
                navigate(peopleHistoryPath(value));
              }
            }}
          />
        }
      >
        <CsvExportButton
          selection={{ query: apiEndpoints.peopleHistory, selection }}
          available={page.page !== null && !page.loading}
        />
      </Toolbar>
      {page.loading ? <p role="status">{p.text("ui.loadingHistory")}</p> : null}
      {page.message === null ? null : (
        <p role="alert">
          <NoticeView value={page.message} />
        </p>
      )}
      <h2 className="sr-only">{p.text("ui.timeline.title")}</h2>
      <ResultPages views={page.views} />

      <ListFooter
        basis={page.page?.basis ?? null}
        continuation={page.cursor}
        loading={page.loading}
        busy={page.refreshBusy}
        load={(cursor) => {
          document.getElementById("people-history-title")?.focus({ preventScroll: true });
          void page.load(cursor);
        }}
        refresh={
          page.page === null || page.message !== null
            ? () => {
                void page.load(null);
              }
            : page.refresh
        }
      />
    </Page>
  );
};
