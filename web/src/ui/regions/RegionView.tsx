import { Section } from "../Section";
import { TechnicalDetails } from "../TechnicalDetails";
import { EmptyState } from "../EmptyState";
import { useId } from "react";
import type { Region, RegionContent } from "@contracts/client";
import { usePresentation } from "../../presentation/context";
import { renderCatalogKey } from "../../presentation/messages";
import { Entries } from "./Entries";
import { DataTable } from "../DataTable";
import { Stepper } from "../Stepper";
import { ChipList, chipValues } from "../ChipList";
import { TaskControls } from "../TaskControls";
import { Notice } from "./Notice";
import { Timeline } from "./Timeline";
import type { RegionInteraction } from "./interaction";
import {} from "./region.css";
const Content = ({
  content,
  timeZone,
  copy,
  avatars,
  emptyId,
  ...interaction
}: RegionInteraction & {
  content: RegionContent;
  timeZone: string;
  copy: boolean;
  avatars: boolean;
  emptyId?: string | undefined;
}) => {
  switch (content.kind) {
    case "ENTRIES": {
      const entries = content.entries.filter((entry) => entry.recorded !== false);
      if (entries.length === 0 && emptyId !== undefined) {
        return <EmptyState messageId={emptyId} />;
      }
      return (
        <Entries
          entries={entries}
          timeZone={timeZone}
          copy={copy}
          avatars={avatars}
          {...(interaction.countLink === undefined ? {} : { countLink: interaction.countLink })}
          {...(interaction.sectionLink === undefined ? {} : { link: interaction.sectionLink })}
        />
      );
    }
    case "TABLE":
      return <DataTable view={content} timeZone={timeZone} emptyId={emptyId} {...interaction} />;
    case "STAGES":
      return <Stepper content={content} timeZone={timeZone} {...interaction} />;
    case "CODES":
      return <ChipList values={chipValues(content)} />;
    case "NOTICE":
      return <Notice content={content} timeZone={timeZone} />;
    case "TIMELINE":
      return <Timeline content={content} timeZone={timeZone} emptyId={emptyId} {...interaction} />;
  }
};
const RegionActions = ({
  region,
  onAction,
  actionsDisabled,
}: Pick<RegionInteraction, "onAction" | "actionsDisabled"> & { region: Region }) => (
  <TaskControls
    disabled={actionsDisabled ?? false}
    actions={region.actions ?? []}
    prominent={region.role === "RECOMMENDATION"}
    {...(onAction === undefined
      ? {}
      : {
          onAction: (action, originId) => {
            onAction(action, undefined, originId);
          },
        })}
    compact
  />
);
export const RegionView = ({
  region,
  timeZone = "UTC",
  headerActions,
  embedded = false,
  ...interaction
}: RegionInteraction & { region: Region; timeZone?: string; embedded?: boolean }) => {
  const p = usePresentation();
  const heading = useId();
  const body = (
    <Content
      emptyId={region.emptyId}
      content={region.content}
      timeZone={timeZone}
      copy={region.role === "DETAILS"}
      avatars={region.role === "WORK" || region.role === "ACCOUNT"}
      {...interaction}
    />
  );
  const actions = (
    <>
      {headerActions}
      <RegionActions region={region} {...interaction} />
    </>
  );
  if (region.role === "DETAILS") {
    return (
      <TechnicalDetails className="screen-details">
        {body}
        {actions}
      </TechnicalDetails>
    );
  }
  if (region.titleId === undefined) {
    return (
      <section className="screen-region" data-region={region.role}>
        {body}
        {actions}
      </section>
    );
  }
  return (
    <Section
      id={heading}
      title={renderCatalogKey(p, region.titleId)}
      count={region.count}
      data-region={region.role}
      role={embedded || region.role === "FACTS" ? "group" : undefined}
      actions={actions}
    >
      {body}
    </Section>
  );
};
