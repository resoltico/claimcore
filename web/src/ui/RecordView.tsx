import "./presented-view.css";
import { useId } from "react";
import type { PresentedSection, PresentedView } from "@contracts/client";
import { PresentedValue } from "../components/PresentedValue";
import { CopyValue } from "../components/CopyValue";
import { usePresentation } from "../presentation/context";
import { renderCatalogKey } from "../presentation/messages";
import { canonicalPresented } from "../presentation/canonicalPresented";

const RecordSection = ({ section, timeZone }: { section: PresentedSection; timeZone: string }) => {
  const p = usePresentation();
  const heading = useId();
  return (
    <section data-section={section.titleId}>
      <h2 id={heading}>{renderCatalogKey(p, section.titleId)}</h2>
      <dl>
        {section.entries.map((entry) => (
          <div key={entry.key} data-prominence={entry.prominence} data-entry={entry.key}>
            <dt>{renderCatalogKey(p, entry.labelId)}</dt>
            <dd>
              <PresentedValue value={entry.value} timeZone={timeZone} />
              {entry.value.kind === "REFERENCE" || entry.value.kind === "IDENTIFIER" ? (
                <CopyValue
                  label={renderCatalogKey(p, entry.labelId)}
                  value={canonicalPresented(entry.value)}
                />
              ) : null}
            </dd>
          </div>
        ))}
      </dl>
    </section>
  );
};
export const RecordView = ({
  view,
  timeZone = "UTC",
}: {
  view: Extract<PresentedView, { kind: "RECORD" }>;
  timeZone?: string;
}) => {
  const p = usePresentation();
  const canonical = view.sections
    .flatMap((section) =>
      section.entries.map((entry) => `${entry.key}: ${canonicalPresented(entry.value)}`),
    )
    .join("\n");
  return (
    <div className="record-view">
      {view.sections.map((section, index) => (
        // oxlint-disable-next-line react/no-array-index-key -- Section positions are supplied by the wire view; repeated titles are valid and these sections own no input state.
        <RecordSection key={`${section.titleId}:${index}`} section={section} timeZone={timeZone} />
      ))}
      {canonical === "" ? null : (
        <CopyValue label={p.text("label.recordDetails")} value={canonical} />
      )}
    </div>
  );
};
