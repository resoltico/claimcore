import { useId, type ReactNode } from "react";
import type { Region, RegionContent, Screen } from "@contracts/client";
import { PresentedValue } from "../components/PresentedValue";
import { usePresentation } from "../presentation/context";
import { renderCatalogKey } from "../presentation/messages";
import { EntriesView, SectionsView } from "./EntriesView";
import { TableView, type RowLink } from "./TableView";
import { SuppliedMessage } from "./SuppliedMessage";
import "./presented-view.css";

const StageContent = ({
  content,
  timeZone,
}: {
  content: Extract<RegionContent, { kind: "STAGES" }>;
  timeZone: string;
}) => {
  const p = usePresentation();
  return (
    <ol className="case-stage-list">
      {content.steps.map((step) => (
        <li
          key={step.stage}
          aria-current={step.stage === content.current ? "step" : undefined}
          data-complete={step.reached}
        >
          <strong>{p.term("caseStage", step.stage)}</strong>
          <span className="sr-only">
            {p.text(step.reached ? "ui.stage.recorded" : "label.notRecorded")}
          </span>
          <span>
            {step.date === null ? (
              p.text(step.reached ? "ui.stage.noDate" : "label.notRecorded")
            ) : (
              <time dateTime={step.date}>{p.date(step.date)}</time>
            )}
          </span>
          {step.amount === null ? null : (
            <PresentedValue value={{ kind: "MONEY", ...step.amount }} timeZone={timeZone} />
          )}
        </li>
      ))}
    </ol>
  );
};
const TimelineContent = ({
  content,
  timeZone,
}: {
  content: Extract<RegionContent, { kind: "TIMELINE" }>;
  timeZone: string;
}) => {
  const p = usePresentation();
  return (
    <ol className="region-timeline">
      {content.entries.map((entry) => (
        <li key={entry.key}>
          <h3>
            <SuppliedMessage message={entry.summary} timeZone={timeZone} />
          </h3>
          <p>
            <PresentedValue value={{ kind: "ACTOR", value: entry.who }} timeZone={timeZone} /> ·{" "}
            <PresentedValue value={{ kind: "INSTANT", value: entry.when }} timeZone={timeZone} />
          </p>
          <SectionsView sections={entry.highlights} timeZone={timeZone} />
          {entry.details.length === 0 ? null : (
            <details>
              <summary>{p.text("label.recordDetails")}</summary>
              <SectionsView sections={entry.details} timeZone={timeZone} />
            </details>
          )}
        </li>
      ))}
    </ol>
  );
};
const ActionContent = ({ content }: { content: Extract<RegionContent, { kind: "ACTIONS" }> }) => {
  const p = usePresentation();
  return (
    <div className="region-actions">
      {content.recommended === null ? null : <strong>{p.actLabel(content.recommended)}</strong>}
      {content.groups.map((group) => (
        <section key={group.family}>
          <h3>{p.term("actFamily", group.family)}</h3>
          <ul>
            {group.acts.map((act) => (
              <li key={act}>{p.actLabel(act)}</li>
            ))}
          </ul>
        </section>
      ))}
    </div>
  );
};
const Content = ({
  content,
  timeZone,
  copy,
  renderActions,
  rowLink,
  labelledBy,
}: {
  content: RegionContent;
  timeZone: string;
  copy: boolean;
  renderActions?: (index: number) => ReactNode;
  rowLink?: (index: number) => RowLink | undefined;
  labelledBy?: string;
}) => {
  const p = usePresentation();
  switch (content.kind) {
    case "ENTRIES":
      return <EntriesView entries={content.entries} timeZone={timeZone} copy={copy} />;
    case "TABLE":
      return (
        <TableView
          view={content}
          timeZone={timeZone}
          {...(renderActions === undefined ? {} : { renderActions })}
          {...(rowLink === undefined ? {} : { rowLink })}
          {...(labelledBy === undefined ? {} : { labelledBy })}
        />
      );
    case "CODES":
      return <PresentedValue value={content} timeZone={timeZone} />;
    case "NOTICE":
      return (
        <div className="region-notice" data-tone={content.tone} role="status">
          <p>{renderCatalogKey(p, content.messageId)}</p>
          {content.reason === undefined ? null : (
            <PresentedValue value={content.reason} timeZone={timeZone} />
          )}
        </div>
      );
    case "STAGES":
      return <StageContent content={content} timeZone={timeZone} />;
    case "ACTIONS":
      return <ActionContent content={content} />;
    case "TIMELINE":
      return <TimelineContent content={content} timeZone={timeZone} />;
  }
};
export const RegionView = ({
  region,
  timeZone = "UTC",
  ...interaction
}: {
  region: Region;
  timeZone?: string;
  renderActions?: (index: number) => ReactNode;
  rowLink?: (index: number) => RowLink | undefined;
  labelledBy?: string;
}) => {
  const p = usePresentation();
  const heading = useId();
  const body = (
    <Content
      content={region.content}
      timeZone={timeZone}
      copy={region.role === "DETAILS"}
      {...interaction}
    />
  );
  return region.role === "DETAILS" ? (
    <details className="screen-details" data-region={region.role}>
      <summary>{renderCatalogKey(p, region.titleId ?? "label.recordDetails")}</summary>
      {body}
    </details>
  ) : (
    <section className="screen-region" data-region={region.role}>
      {region.titleId === undefined ? null : (
        <h2 id={heading}>{renderCatalogKey(p, region.titleId)}</h2>
      )}
      {body}
    </section>
  );
};
export const ScreenView = ({
  view,
  timeZone = "UTC",
  roles,
  ...interaction
}: {
  view: Screen;
  timeZone?: string;
  roles?: ReadonlyArray<Region["role"]>;
  renderActions?: (index: number) => ReactNode;
  rowLink?: (index: number) => RowLink | undefined;
  labelledBy?: string;
}) => (
  <div className="screen-view">
    {view.regions
      .filter((region) => roles === undefined || roles.includes(region.role))
      .map((region, index) => (
        <RegionView
          // oxlint-disable-next-line react/no-array-index-key -- FACTS regions can repeat, and regions own no input state.
          key={`${region.role}:${index}`}
          region={region}
          timeZone={timeZone}
          {...interaction}
        />
      ))}
  </div>
);
