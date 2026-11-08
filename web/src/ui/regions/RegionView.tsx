import { useId } from "react";
import type { Region, RegionContent } from "@contracts/client";
import { usePresentation } from "../../presentation/context";
import { renderCatalogKey } from "../../presentation/messages";
import { Entries } from "./Entries";
import { Table } from "./Table";
import { Stages } from "./Stages";
import { Codes } from "./Codes";
import { Actions } from "./Actions";
import { Notice } from "./Notice";
import { Timeline } from "./Timeline";
import type { RegionInteraction } from "./interaction";
import "./region.css";
const Content = ({
  content,
  timeZone,
  copy,
  avatars,
  ...interaction
}: RegionInteraction & {
  content: RegionContent;
  timeZone: string;
  copy: boolean;
  avatars: boolean;
}) => {
  switch (content.kind) {
    case "ENTRIES":
      return (
        <Entries
          entries={content.entries}
          timeZone={timeZone}
          copy={copy}
          avatars={avatars}
          {...(interaction.countLink === undefined ? {} : { countLink: interaction.countLink })}
          {...(interaction.sectionLink === undefined ? {} : { link: interaction.sectionLink })}
        />
      );
    case "TABLE":
      return <Table view={content} timeZone={timeZone} {...interaction} />;
    case "STAGES":
      return <Stages content={content} timeZone={timeZone} />;
    case "CODES":
      return <Codes content={content} />;
    case "ACTIONS":
      return (
        <Actions
          content={content}
          {...(interaction.onAct === undefined ? {} : { onAct: interaction.onAct })}
        />
      );
    case "NOTICE":
      return <Notice content={content} timeZone={timeZone} />;
    case "TIMELINE":
      return (
        <Timeline
          content={content}
          timeZone={timeZone}
          {...(interaction.rowLink === undefined ? {} : { rowLink: interaction.rowLink })}
        />
      );
  }
};
export const RegionView = ({
  region,
  timeZone = "UTC",
  headerActions,
  ...interaction
}: RegionInteraction & { region: Region; timeZone?: string }) => {
  const p = usePresentation();
  const heading = useId();
  const body = (
    <Content
      content={region.content}
      timeZone={timeZone}
      copy={region.role === "DETAILS"}
      avatars={region.role === "WORK" || region.role === "ACCOUNT"}
      {...interaction}
    />
  );
  return region.role === "DETAILS" ? (
    <details className="screen-details" data-region={region.role}>
      <summary>{renderCatalogKey(p, region.titleId ?? "label.recordDetails")}</summary>
      {body}
    </details>
  ) : (
    <section
      className="screen-region"
      data-region={region.role}
      role={region.role === "FACTS" ? "group" : undefined}
      {...(region.titleId === undefined ? {} : { "aria-labelledby": heading })}
    >
      {region.titleId === undefined && headerActions === undefined ? null : (
        <div className="region-heading">
          {region.titleId === undefined ? null : (
            <h2 id={heading}>{renderCatalogKey(p, region.titleId)}</h2>
          )}
          {headerActions}
        </div>
      )}
      {body}
    </section>
  );
};
