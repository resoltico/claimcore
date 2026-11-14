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
import {} from "./region.css";
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
    case "NOTICE":
      return <Notice content={content} timeZone={timeZone} />;
    case "TIMELINE":
      return (
        <Timeline
          content={content}
          timeZone={timeZone}
          {...(interaction.onAction === undefined
            ? {}
            : {
                onAction: (action, originId) => interaction.onAction?.(action, undefined, originId),
              })}
        />
      );
  }
};
const RegionActions = ({
  region,
  onAction,
  actionsDisabled,
}: Pick<RegionInteraction, "onAction" | "actionsDisabled"> & { region: Region }) => (
  <Actions
    disabled={actionsDisabled ?? false}
    actions={region.actions ?? []}
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
    <>
      <Content
        content={region.content}
        timeZone={timeZone}
        copy={region.role === "DETAILS"}
        avatars={region.role === "WORK" || region.role === "ACCOUNT"}
        {...interaction}
      />
      <RegionActions region={region} {...interaction} />
    </>
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
      role={embedded || region.role === "FACTS" ? "group" : undefined}
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
