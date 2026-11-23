import { useState } from "react";
import type { RegionInteraction, RowLink } from "./interaction";
import type { RegionContent, RegionTimelineEntry } from "@contracts/client";
import { usePresentation } from "../../presentation/context";
import { PresentedValue } from "../../components/PresentedValue";
import { SectionsView } from "../SectionsView";
import { renderCatalogKey } from "../../presentation/messages";
import { followLink } from "../../routing/followLink";
import { SuppliedMessage } from "../SuppliedMessage";
import { Button } from "../Button";
import { Dialog } from "../Dialog";
import { coreRowLink } from "./rowLink";
import "./timeline.css";

const TimelineTarget = ({
  value,
  link,
  timeZone,
}: {
  value: RegionTimelineEntry["target"];
  link: RowLink | undefined;
  timeZone: string;
}) => {
  if (value === undefined) {
    return null;
  }
  const field = <PresentedValue value={value} timeZone={timeZone} />;
  return link === undefined ? (
    field
  ) : (
    <a
      href={link.href}
      id={link.id}
      onClick={(event) => {
        followLink(event, link.follow);
      }}
    >
      {field}
    </a>
  );
};
export const Timeline = ({
  content,
  timeZone,
  linkNavigation,
  emptyId,
}: RegionInteraction & {
  content: Extract<RegionContent, { kind: "TIMELINE" }>;
  timeZone: string;
  emptyId?: string | undefined;
}) => {
  const p = usePresentation();
  const [selectedKey, setSelectedKey] = useState<string | null>(null);
  const selected = content.entries.find((entry) => entry.key === selectedKey) ?? null;
  return (
    <>
      {content.entries.length === 0 && emptyId !== undefined ? (
        <p>{renderCatalogKey(p, emptyId)}</p>
      ) : (
        <ol className="region-timeline">
          {content.entries.map((entry) => {
            const link = coreRowLink(entry.link, linkNavigation);
            return (
              <li key={entry.key}>
                <PresentedValue
                  value={{ kind: "INSTANT", value: entry.when }}
                  timeZone={timeZone}
                />
                <span className="timeline-event">
                  <SuppliedMessage message={entry.summary} timeZone={timeZone} />
                  <TimelineTarget value={entry.target} link={link} timeZone={timeZone} />
                </span>
                <PresentedValue value={entry.who} timeZone={timeZone} />
                <Button
                  variant="quiet"
                  compact
                  onPress={() => {
                    setSelectedKey(entry.key);
                  }}
                >
                  {p.text("ui.viewDetails")}
                </Button>
              </li>
            );
          })}
        </ol>
      )}
      <Dialog
        title={p.text("ui.viewDetails")}
        closeLabel={p.text("ui.close")}
        isOpen={selected !== null}
        isDismissable
        onOpenChange={(open) => {
          if (!open) {
            setSelectedKey(null);
          }
        }}
      >
        {selected === null ? null : (
          <>
            {selected.target === undefined ? null : (
              <PresentedValue value={selected.target} timeZone={timeZone} />
            )}
            <SectionsView sections={selected.highlights} timeZone={timeZone} />
            {(selected.changes ?? []).length === 0 ? null : (
              <dl className="timeline-comparison" aria-label={p.text("label.changes")}>
                {selected.changes?.map((change) => (
                  <div key={change.key} data-change-label={change.labelId}>
                    <dt>{renderCatalogKey(p, change.labelId)}</dt>
                    <dd data-side="before">
                      {p.text("label.before")}{" "}
                      <PresentedValue value={change.before} timeZone={timeZone} />
                    </dd>
                    <dd data-side="after">
                      {p.text("label.after")}{" "}
                      <PresentedValue value={change.after} timeZone={timeZone} />
                    </dd>
                  </div>
                ))}
              </dl>
            )}
            <SectionsView sections={selected.details} headingLevel={3} timeZone={timeZone} />
          </>
        )}
      </Dialog>
    </>
  );
};
