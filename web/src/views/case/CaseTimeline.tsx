import { CsvExportButton } from "../../components/CsvExportButton";
import { attachmentDisclosureOf } from "../../api/attachmentDisclosure";
import { useEndpointCache } from "../../hooks/EndpointProvider";
import type { PresentedPage } from "../../protocol/types";
import type { EndpointCache } from "../../hooks/endpointCache";
import { apiEndpoints } from "../../api/service";
import {
  type ExportRequest,
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
import {} from "./timeline.css";
const select = (reply: HumanResponseByEndpoint[typeof apiEndpoints.caseTimeline]) => {
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
    nextCursor: doc.nextCursor,
    basis: doc.basis.observation,
    disclosure: doc.disclosure,
    attachmentDisclosure: attachmentDisclosureOf(doc),
  };
};
type TimelinePage = NonNullable<ReturnType<typeof select>>;
const timelineContinuation = (page: TimelinePage) =>
  String(page.attachmentDisclosure?.sequence ?? "");

type TimelineInput = (cursor: string | null) => CaseTimelineRequest;

const useTimelineAdmission = (input: TimelineInput) => {
  const cache = useEndpointCache()!;
  const [erasedReadback, setErasedReadback] = useState<{
    cache: EndpointCache;
    generation: number;
    input: TimelineInput;
    page: TimelinePage;
  } | null>(null);

  const admit = useCallback(
    (response: HumanResponseByEndpoint[typeof apiEndpoints.caseTimeline]) => {
      const page = select(response);
      const observation = page?.attachmentDisclosure;
      const { target } = input(null);
      if (observation && target.kind === "ID" && observation.caseId !== target.caseId) {
        return null;
      }
      if (page?.disclosure === "ERASED") {
        if (observation && cache.observeAttachmentErasure(observation.caseId)) {
          setErasedReadback({ cache, generation: cache.invalidation(), input, page });
        }
        return page;
      }
      return observation &&
        !cache.observeAttachmentDisclosure(observation.caseId, observation.sequence)
        ? null
        : page;
    },
    [cache, input],
  );

  const visibleViews = (loading: boolean, views: ReadonlyArray<PresentedPage>) => {
    if (!loading && erasedReadback !== null) {
      setErasedReadback(null);
    }
    // This erased page was already admitted by the current request. Keep its structural
    // history until the erasure-triggered revalidation finishes, within the same scope.
    return loading &&
      erasedReadback?.cache === cache &&
      erasedReadback.input === input &&
      erasedReadback.generation === cache.invalidation()
      ? [{ view: erasedReadback.page.screen, basis: erasedReadback.page.basis }]
      : views;
  };

  return { admit, visibleViews };
};

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
const TimelineCsvExport = ({
  target,
  filter,
  page,
  suppressed,
  loading,
}: {
  target: CaseSelector;
  filter: TimelineFilter;
  page: TimelinePage | null;
  suppressed: boolean;
  loading: boolean;
}) => {
  const selection: ExportRequest = {
    query: apiEndpoints.caseTimeline,
    selection: { target, ...(filter === "ALL" ? {} : { filter }) },
  };
  return (
    <CsvExportButton selection={selection} available={page !== null && !suppressed && !loading} />
  );
};
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
  const { admit, visibleViews } = useTimelineAdmission(input);
  const result = useEndpointPage<
    typeof apiEndpoints.caseTimeline,
    RegionTimelineEntry,
    TimelinePage
  >(apiEndpoints.caseTimeline, input, admit, onDisclosureChanged, undefined, timelineContinuation);
  const suppressed = useTimelineDisclosure(result.page, disclosure, result.loading, result.load);
  const views = visibleViews(result.loading, result.views);

  return (
    <section
      className="case-timeline-panel"
      aria-labelledby="timeline-title"
      aria-busy={result.loading}
    >
      <div className="section-heading">
        <h2 id="timeline-title">{p.text("ui.timeline.title")}</h2>
        <TimelineCsvExport
          target={target}
          filter={filter}
          page={result.page}
          suppressed={suppressed}
          loading={result.loading}
        />
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
        <PageTables views={suppressed ? [] : views} />
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
