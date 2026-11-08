import { PageTables } from "../../ui/PageTables";
import { useCallback, useState } from "react";
import type {
  CaseSelector,
  CaseTimelineEntry,
  CaseTimelinePage,
  ResponseByEndpoint,
} from "@contracts/client";
import { Button } from "../../ui/Button";
import { useEndpointPage } from "../../hooks/useEndpointPage";
import { NoticeView } from "../../presentation/Message";
import { usePresentation } from "../../presentation/context";
import "./timeline.css";
const select = (reply: ResponseByEndpoint["case.timeline"]): CaseTimelinePage | null =>
  reply.outcome.kind === "SUCCEEDED" ? reply.outcome.data : null;
export type TimelineFilter = "ALL" | "FACTS" | "WORK";
const selectorValue = (target: CaseSelector): string =>
  target.kind === "ID" ? target.caseId : target.caseReference;
const filterLabels = {
  ALL: "ui.timeline.all",
  FACTS: "ui.timeline.facts",
  WORK: "ui.timeline.work",
} as const;
export const CaseTimeline = ({
  target,
  onDisclosureRefused,
  selection,
}: {
  target: CaseSelector;
  onDisclosureRefused?: () => void;
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
  const result = useEndpointPage<"case.timeline", CaseTimelineEntry, CaseTimelinePage>(
    "case.timeline",
    input,
    select,
    onDisclosureRefused,
  );

  return (
    <section
      className="case-timeline-panel"
      aria-labelledby="timeline-title"
      aria-busy={result.loading}
    >
      <div className="section-heading">
        <h2 id="timeline-title">{p.text("ui.timeline.title")}</h2>
        <div className="timeline-filters" role="group" aria-label={p.text("ui.timeline.filter")}>
          {(["ALL", "FACTS", "WORK"] as const).map((value) => (
            <Button
              variant="secondary"
              key={value}
              aria-pressed={filter === value}
              onPress={() => {
                if (selection === undefined) {
                  setLocalFilter(value);
                } else {
                  selection.change(value);
                }
              }}
            >
              {p.text(filterLabels[value])}
            </Button>
          ))}
        </div>
      </div>
      {result.message === null ? null : (
        <p role="alert">
          <NoticeView value={result.message} />
        </p>
      )}
      {result.loading ? <p role="status">{p.text("ui.loading")}</p> : null}
      <PageTables views={result.views} />
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
    </section>
  );
};
