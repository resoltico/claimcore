import "./presented-view.css";
import { useId } from "react";
import type { PresentedEntry, PresentedSection } from "@contracts/client";
import { PresentedValue } from "../components/PresentedValue";
import { CopyValue } from "../components/CopyValue";
import { usePresentation } from "../presentation/context";
import { renderCatalogKey } from "../presentation/messages";
import { canonicalPresented } from "../presentation/canonicalPresented";

export const EntriesView = ({
  entries,
  timeZone = "UTC",
  copy = false,
}: {
  entries: ReadonlyArray<PresentedEntry>;
  timeZone?: string;
  copy?: boolean;
}) => {
  const p = usePresentation();
  return (
    <div className="entries-view">
      <dl>
        {entries.map((entry) => (
          <div key={entry.key} data-prominence={entry.prominence} data-entry={entry.key}>
            <dt>{renderCatalogKey(p, entry.labelId)}</dt>
            <dd>
              <PresentedValue value={entry.value} timeZone={timeZone} />
              {copy && (entry.value.kind === "REFERENCE" || entry.value.kind === "IDENTIFIER") ? (
                <CopyValue
                  label={renderCatalogKey(p, entry.labelId)}
                  value={canonicalPresented(entry.value)}
                />
              ) : null}
            </dd>
          </div>
        ))}
      </dl>
      {copy && entries.length > 0 ? (
        <CopyValue
          label={p.text("label.recordDetails")}
          value={entries
            .map((entry) => `${entry.key}: ${canonicalPresented(entry.value)}`)
            .join("\n")}
        />
      ) : null}
    </div>
  );
};
const EntrySection = ({ section, timeZone }: { section: PresentedSection; timeZone: string }) => {
  const p = usePresentation();
  const heading = useId();
  return (
    <section data-section={section.titleId} aria-labelledby={heading}>
      <h3 id={heading}>{renderCatalogKey(p, section.titleId)}</h3>
      <EntriesView entries={section.entries} timeZone={timeZone} />
    </section>
  );
};
export const SectionsView = ({
  sections,
  timeZone = "UTC",
}: {
  sections: ReadonlyArray<PresentedSection>;
  timeZone?: string;
}) => (
  <div className="record-view">
    {sections.map((section, index) => (
      // oxlint-disable-next-line react/no-array-index-key -- Ordered supplied sections can repeat titles.
      <EntrySection key={`${section.titleId}:${index}`} section={section} timeZone={timeZone} />
    ))}
  </div>
);
