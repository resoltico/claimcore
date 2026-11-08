import type { PresentedValue as Value, Region } from "@contracts/client";
import { PresentedValue } from "../../components/PresentedValue";
import { Entries } from "../../ui/regions/Entries";
import { RegionView } from "../../ui/regions/RegionView";

export const CaseIdentity = ({
  region,
  attention,
  timeZone,
  requestedReference,
}: {
  region: Region | undefined;
  attention: Region | undefined;
  timeZone: string;
  requestedReference: string | undefined;
}) => {
  const entries = region?.content.kind === "ENTRIES" ? region.content.entries : [];
  const reference = entries.find((entry) => entry.value.kind === "REFERENCE");
  const chips = entries.filter((entry) => ["STATE", "CODE", "CODES"].includes(entry.value.kind));
  const facts = entries.filter((entry) => entry !== reference && !chips.includes(entry));
  let title: Value = { kind: "MESSAGE", messageId: "ui.caseDetail" };
  if (reference !== undefined) {
    title = reference.value;
  } else if (requestedReference !== undefined) {
    title = { kind: "REFERENCE", value: requestedReference };
  }
  return (
    <div className="case-identity" data-region="IDENTITY">
      <div className="case-title-line">
        <h1 id="case-detail-title" tabIndex={-1}>
          <PresentedValue value={title} timeZone={timeZone} />
        </h1>
        <div className="case-header-state">
          {chips.map((entry) => (
            <PresentedValue key={entry.key} value={entry.value} timeZone={timeZone} />
          ))}
          {attention === undefined ? null : <RegionView region={attention} timeZone={timeZone} />}
        </div>
      </div>
      {facts.length === 0 ? null : (
        <div className="case-identity-facts">
          <Entries entries={facts} timeZone={timeZone} />
        </div>
      )}
    </div>
  );
};
