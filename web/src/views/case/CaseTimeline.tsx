import {
  taskChoices,
  type CaseTimelineRequest,
  type HistoryDisclosure,
  type CaseSelector,
  type RegionTimelineEntry,
  type HumanResponseByEndpoint,
} from "@contracts/client";
import { isTerm } from "../../presentation/terms";
import { permitsHistory } from "../../api/historyDisclosure";
import { ViewTabs } from "../../ui/ViewTabs";
import { PageTables } from "../../ui/PageTables";
import { useCallback, useState, useEffect, useRef } from "react";

import { Button } from "../../ui/Button";
import { useEndpointPage } from "../../hooks/useEndpointPage";
import { NoticeView } from "../../presentation/Message";
import { usePresentation } from "../../presentation/context";
import "./timeline.css";
const select = (reply: HumanResponseByEndpoint["case.timeline"]) => {
  if (reply.outcome.kind !== "SUCCEEDED") {
    return null;
  }
  const doc = reply.outcome.document;
  if (
    doc.basis.kind !== "SNAPSHOT" ||
    (doc.disclosure !== "FULL" && doc.disclosure !== "SUMMARY" && doc.disclosure !== "ERASED")
  ) {
    return null;
  }
  return {
    items: doc.screen.regions.flatMap((region) =>
      region.content.kind === "TIMELINE" ? region.content.entries : [],
    ),
    screen: doc.screen,
    nextCursor: doc.context.nextCursor,
    basis: doc.basis.observation,
    disclosure: doc.disclosure,
  };
};
type TimelinePage = NonNullable<ReturnType<typeof select>>;
const useTimelineDisclosure = (
  page: TimelinePage | null,
  disclosure: HistoryDisclosure,
  loading: boolean,
  load: (cursor: string | null) => Promise<void>,
) => {
  const suppressed = page !== null && !permitsHistory(disclosure, page.disclosure);
  const requestedFor = useRef<HistoryDisclosure | null>(null);
  useEffect(() => {
    if (!suppressed && page !== null) {
      requestedFor.current = null;
    } else if (suppressed && !loading && requestedFor.current !== disclosure) {
      requestedFor.current = disclosure;
      void load(null);
    }
  }, [suppressed, loading, load, disclosure, page]);
  return suppressed;
};
const filterOptions = taskChoices.timelineFilter;
export type TimelineFilter = "ALL" | NonNullable<CaseTimelineRequest["filter"]>;
const selectorValue = (target: CaseSelector): string =>
  target.kind === "ID" ? target.caseId : target.caseReference;
export const CaseTimeline = ({
  target,
  onDisclosureChanged,
  selection,
  disclosure = "FULL",
}: {
  target: CaseSelector;
  disclosure?: HistoryDisclosure;
  onDisclosureChanged?: () => void;
  selection?: { value: TimelineFilter; change: (value: TimelineFilter) => void };
}) => {
  const p = usePresentation();
  const [localFilter, setLocalFilter] = useState<TimelineFilter>("ALL");
  const filter = selection?.value ?? localFilter;
  const targetKind = target.kind;
  const targetValue = selectorValue(target);
  const input = useCallback(
    (cursor: string | null) => ({
      target:
        targetKind === "ID"
          ? { kind: targetKind, caseId: targetValue }
          : { kind: targetKind, caseReference: targetValue },
      limit: 50,
      ...(filter === "ALL" ? {} : { filter }),
      ...(cursor === null ? {} : { cursor }),
    }),
    [targetKind, targetValue, filter],
  );
  const result = useEndpointPage<"case.timeline", RegionTimelineEntry, TimelinePage>(
    "case.timeline",
    input,
    select,
    onDisclosureChanged,
  );
  const suppressed = useTimelineDisclosure(result.page, disclosure, result.loading, result.load);

  return (
    <section
      className="case-timeline-panel"
      aria-labelledby="timeline-title"
      aria-busy={result.loading}
    >
      <div className="section-heading">
        <h2 id="timeline-title">{p.text("ui.timeline.title")}</h2>
      </div>
      <ViewTabs
        items={[
          { id: "ALL", label: p.text("ui.timeline.all"), count: "" },
          ...filterOptions.map((value) => ({
            id: value,
            label: p.term("timelineFilter", value),
            count: "",
          })),
        ]}
        selected={filter}
        label={p.text("ui.timeline.filter")}
        loading={result.loading}
        controls={null}
        onSelect={(value) => {
          if (
            value === "ALL" ||
            (isTerm("timelineFilter", value) && filterOptions.includes(value))
          ) {
            if (selection === undefined) {
              setLocalFilter(value);
            } else {
              selection.change(value);
            }
          }
        }}
      >
        {result.message === null ? null : (
          <p role="alert">
            <NoticeView value={result.message} />
          </p>
        )}
        {result.loading ? <p role="status">{p.text("ui.loading")}</p> : null}
        <PageTables views={suppressed ? [] : result.views} />
        {result.page !== null && result.items.length === 0 && !result.loading ? (
          <p>{p.text("ui.timeline.empty")}</p>
        ) : null}
        {result.message === null ? null : (
          <Button isDisabled={result.loading} onPress={() => void result.load(null)}>
            {p.text("ui.tryAgain")}
          </Button>
        )}
        {result.cursor === null ? null : (
          <Button isDisabled={result.loading} onPress={() => void result.load(result.cursor)}>
            {p.text("ui.timeline.more")}
          </Button>
        )}
      </ViewTabs>
    </section>
  );
};
