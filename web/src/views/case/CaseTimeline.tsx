import { useCallback, useState } from "react";
import { resolveTimelineHighlights } from "@contracts/client";
import type {
  CaseSelector,
  CaseTimelineEntry,
  CaseTimelinePage,
  TimelineChanges as Changes,
  ResponseByEndpoint,
} from "@contracts/client";
import { Button } from "../../ui/Button";
import { ActorIdentity, RecordedTime } from "../../components/ActorIdentity";
import { CopyValue } from "../../components/CopyValue";
import { useEndpointPage } from "../../hooks/useEndpointPage";
import { NoticeView } from "../../presentation/Message";
import { usePresentation } from "../../presentation/context";
import { renderCatalogKey } from "../../presentation/messages";
import { productMetadata } from "../../protocol/productMetadata";
import { RelativeTime } from "../../ui/RelativeTime";
import { TimelineChanges, TimelineChangeList } from "./TimelineChanges";
import { TimelineEventView } from "./TimelineEventView";
import "./timeline.css";
const select = (reply: ResponseByEndpoint["case.timeline"]): CaseTimelinePage | null =>
  reply.outcome.kind === "SUCCEEDED" ? reply.outcome.data : null;
const TimelineEntryHighlights = ({
  changes,
  detail,
}: {
  changes: Changes;
  detail: "CURRENT" | "SUMMARY" | "ERASED";
}) => {
  const p = usePresentation();
  if (detail === "CURRENT") {
    return changes.kind === "DISCLOSED" ? (
      <TimelineChangeList items={resolveTimelineHighlights(changes)} compact />
    ) : (
      <TimelineChanges changes={changes} />
    );
  }
  return <p>{p.text(detail === "ERASED" ? "lifecycle.ERASED.meaning" : "ui.timeline.withheld")}</p>;
};
const TimelineEntry = ({
  entry,
  observedAt,
  detail,
}: {
  entry: CaseTimelineEntry;
  observedAt: string | null;
  detail: "CURRENT" | "SUMMARY" | "ERASED";
}) => {
  const p = usePresentation();
  const subject = entry.kind === "CASE_FACT" ? "CASE" : entry.kind;
  const definition = productMetadata.catalog.entries.find(
    (candidate) =>
      candidate.subject === subject &&
      candidate.constants.some(
        (constant) => constant.path.join(".") === "act.kind" && constant.value === entry.act,
      ),
  );
  const actLabel = definition === undefined ? entry.act : renderCatalogKey(p, definition.labelId);
  return (
    <li className="timeline-entry">
      <details>
        <summary className="timeline-entry-heading">
          <strong>
            {detail === "CURRENT" && entry.changes.kind === "DISCLOSED" ? (
              <TimelineEventView event={entry.changes.overview.event} />
            ) : (
              actLabel
            )}
          </strong>{" "}
          <span className="timeline-attribution">
            <ActorIdentity actor={entry.recordedBy} /> ·{" "}
            {observedAt === null ? (
              <RecordedTime value={entry.recordedAt} />
            ) : (
              <RelativeTime value={entry.recordedAt} observedAt={observedAt} />
            )}
          </span>{" "}
          <span className="timeline-details-label">{p.text("ui.timeline.details")}</span>
        </summary>
        {detail === "CURRENT" && entry.changes.kind === "DISCLOSED" ? (
          <TimelineChanges changes={entry.changes} />
        ) : null}
        <dl className="timeline-record-details">
          <div>
            <dt>{p.fieldLabel("operationId")}</dt>
            <dd>
              <bdi dir="ltr">{entry.operationId}</bdi>
              <CopyValue icon label={p.fieldLabel("operationId")} value={entry.operationId} />
            </dd>
          </div>
          <div>
            <dt>{p.text("label.revisionLabel")}</dt>
            <dd>{p.integer(entry.revision)}</dd>
          </div>
          <div>
            <dt>{p.text("ui.actor")}</dt>
            <dd>
              <bdi dir="ltr">{entry.recordedBy.actorId}</bdi>
            </dd>
          </div>
          <div>
            <dt>{p.text("ui.exactUtc")}</dt>
            <dd>
              <time dateTime={entry.recordedAt}>{entry.recordedAt}</time>
            </dd>
          </div>
          {entry.kind === "REMINDER" ? (
            <>
              <div>
                <dt>{p.text("ui.reminderDetails")}</dt>
                <dd>
                  <bdi dir="ltr">{entry.reminderId}</bdi>
                </dd>
              </div>
              {entry.next === null ? null : (
                <div>
                  <dt>{p.text("ui.timeline.nextReminder")}</dt>
                  <dd>
                    <bdi dir="ltr">{entry.next.reminderId}</bdi> · {p.integer(entry.next.revision)}
                  </dd>
                </div>
              )}
            </>
          ) : null}
        </dl>
      </details>
      <TimelineEntryHighlights changes={entry.changes} detail={detail} />
    </li>
  );
};
export const TimelineEntries = ({
  items,
  observedAt,
  detail,
}: {
  items: ReadonlyArray<CaseTimelineEntry>;
  observedAt: string | null;
  detail: "CURRENT" | "SUMMARY" | "ERASED";
}) => (
  <ol className="case-timeline">
    {items.map((entry) => (
      <TimelineEntry
        key={`${entry.position.sequence}:${entry.position.entryHash}`}
        entry={entry}
        observedAt={observedAt}
        detail={detail}
      />
    ))}
  </ol>
);
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
  const observedAt = result.page?.basis.observedAt;
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
      {observedAt === undefined ? null : (
        <TimelineEntries items={result.items} observedAt={observedAt} detail="CURRENT" />
      )}
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
