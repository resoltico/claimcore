import { useId } from "react";
import type { PresentedSection } from "@contracts/client";
import { usePresentation } from "../presentation/context";
import { renderCatalogKey } from "../presentation/messages";
import type { RowLink } from "./regions/Table";
import { Entries } from "./regions/Entries";
const EntrySection = ({
  section,
  timeZone,
  link,
  headingLevel = 3,
}: {
  section: PresentedSection;
  timeZone: string;
  link?: RowLink;
  headingLevel?: 2 | 3 | 4;
}) => {
  const p = usePresentation();
  const heading = useId();
  const Heading = ({ 2: "h2", 3: "h3", 4: "h4" } as const)[headingLevel];
  return (
    <div role="group" data-section={section.titleId} aria-labelledby={heading}>
      <Heading id={heading}>{renderCatalogKey(p, section.titleId)}</Heading>
      <Entries
        entries={section.entries}
        timeZone={timeZone}
        {...(link === undefined ? {} : { link })}
      />
    </div>
  );
};
export const SectionsView = ({
  sections,
  timeZone = "UTC",
  link,
  headingLevel = 3,
}: {
  sections: ReadonlyArray<PresentedSection>;
  timeZone?: string;
  link?: RowLink;
  headingLevel?: 2 | 3 | 4;
}) => (
  <div className="record-view">
    {sections.map((section, index) => (
      <EntrySection
        // oxlint-disable-next-line react/no-array-index-key -- Ordered supplied read-only sections can repeat titles.
        key={`${section.titleId}:${index}`}
        section={section}
        timeZone={timeZone}
        headingLevel={headingLevel}
        {...(link === undefined ? {} : { link })}
      />
    ))}
  </div>
);
