import type { RegionInteraction, RowLink } from "./interaction";
import type { RegionContent, RegionTimelineEntry } from "@contracts/client";
import { usePresentation } from "../../presentation/context";
import { PresentedValue } from "../../components/PresentedValue";
import { SectionsView } from "../SectionsView";
import { coreRowLink } from "./rowLink";
import { Actions } from "./Actions";

import {} from "./timeline.css";
import { renderCatalogKey } from "../../presentation/messages";
import { followLink } from "../../routing/followLink";
import { SuppliedMessage } from "../SuppliedMessage";
const TimelineTarget = ({
  target,
  rowLink,
  timeZone,
}: {
  target: RegionTimelineEntry["target"];
  rowLink: RowLink | undefined;
  timeZone: string;
}) => {
  if (target === undefined) {
    return null;
  }
  const value = <PresentedValue value={target} timeZone={timeZone} />;
  return rowLink === undefined ? (
    value
  ) : (
    <a
      href={rowLink.href}
      onClick={(event) => {
        followLink(event, rowLink.follow);
      }}
    >
      {value}
    </a>
  );
};
export const Timeline = ({
  content,
  timeZone,
  onAction,
  actionsDisabled,
  actionPresentation,
  linkNavigation,
}: RegionInteraction & {
  content: Extract<RegionContent, { kind: "TIMELINE" }>;
  timeZone: string;
}) => {
  const p = usePresentation();
  return (
    <ol className="region-timeline">
      {content.entries.map((entry) => {
        const rowLink = coreRowLink(entry.link, linkNavigation);
        const changes = entry.changes ?? [];
        return (
          <li key={entry.key} data-effect={entry.key}>
            <h3>
              <SuppliedMessage message={entry.summary} timeZone={timeZone} />
            </h3>
            <TimelineTarget target={entry.target} rowLink={rowLink} timeZone={timeZone} />
            <p>
              <PresentedValue value={entry.who} timeZone={timeZone} /> ·{" "}
              <PresentedValue value={{ kind: "INSTANT", value: entry.when }} timeZone={timeZone} />
            </p>
            <div className="timeline-change-summary">
              {entry.highlights
                .flatMap(({ entries }) => entries)
                .map((item) => (
                  <span key={item.key}>
                    {renderCatalogKey(p, item.labelId)}{" "}
                    <PresentedValue value={item.value} timeZone={timeZone} />
                  </span>
                ))}
            </div>
            {entry.changedFields === undefined ? null : (
              <ul className="timeline-changed-fields" aria-label={p.text("label.changes")}>
                {entry.changedFields.map((field) => (
                  <li key={field}>{renderCatalogKey(p, field)}</li>
                ))}
              </ul>
            )}
            {entry.details.length === 0 && changes.length === 0 ? null : (
              <details>
                <summary>{p.text("label.recordDetails")}</summary>
                {changes.length > 0 ? (
                  <dl className="timeline-comparison" aria-label={p.text("label.changes")}>
                    {changes.map((change) => (
                      <div key={change.key} data-change-label={change.labelId}>
                        <dt>{renderCatalogKey(p, change.labelId)}</dt>
                        <dd data-side="before">
                          <span>{p.text("label.before")}</span>{" "}
                          <PresentedValue value={change.before} timeZone={timeZone} />
                        </dd>
                        <dd data-side="after">
                          <span>{p.text("label.after")}</span>{" "}
                          <PresentedValue value={change.after} timeZone={timeZone} />
                        </dd>
                      </div>
                    ))}
                  </dl>
                ) : null}
                <SectionsView sections={entry.details} headingLevel={4} timeZone={timeZone} />
              </details>
            )}
            <Actions
              actions={entry.actions ?? []}
              disabled={actionsDisabled ?? false}
              {...(actionPresentation === undefined ? {} : { actionPresentation })}
              {...(onAction === undefined
                ? {}
                : {
                    onAction: (action, originId) => {
                      onAction(action, undefined, originId);
                    },
                  })}
              compact
            />
          </li>
        );
      })}
    </ol>
  );
};
