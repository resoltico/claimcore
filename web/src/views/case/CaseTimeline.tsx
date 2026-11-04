import { useCallback, useState } from "react";
import type {
  CaseSelector,
  CaseTimelineEntry,
  CaseTimelinePage,
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
import { TimelineChanges } from "./TimelineChanges";
import "./timeline.css";
const select = (reply: ResponseByEndpoint["case.timeline"]): CaseTimelinePage | null =>
  reply.outcome.kind === "SUCCEEDED" ? reply.outcome.data : null;
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
  return (
    <li className="timeline-entry">
      <p>
        <strong>
          {definition === undefined ? entry.act : renderCatalogKey(p, definition.labelId)}
        </strong>
      </p>
      <p className="timeline-attribution">
        <ActorIdentity actor={entry.recordedBy} /> ·{" "}
        {observedAt === null ? (
          <RecordedTime value={entry.recordedAt} />
        ) : (
          <RelativeTime value={entry.recordedAt} observedAt={observedAt} />
        )}
      </p>
      {detail === "CURRENT" ? (
        <TimelineChanges changes={entry.changes} />
      ) : (
        <p>{p.text(detail === "ERASED" ? "lifecycle.ERASED.meaning" : "ui.timeline.withheld")}</p>
      )}
      <details>
        <summary>{p.text("ui.timeline.details")}</summary>
        <dl>
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
type Filter = "ALL" | "FACTS" | "WORK";
const filterLabels = {
  ALL: "ui.timeline.all",
  FACTS: "ui.timeline.facts",
  WORK: "ui.timeline.work",
} as const;
export const CaseTimeline = ({
  target,
  onDisclosureRefused,
}: {
  target: CaseSelector;
  onDisclosureRefused?: () => void;
}) => {
  const p = usePresentation();
  const [filter, setFilter] = useState<Filter>("ALL");
  const input = useCallback(
    (cursor: string | null) => ({
      target,
      limit: 50,
      ...(filter === "ALL" ? {} : { filter }),
      ...(cursor === null ? {} : { cursor }),
    }),
    [target, filter],
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
                setFilter(value);
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
