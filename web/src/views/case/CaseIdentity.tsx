import type { ReactNode } from "react";
import type { PresentedValue as Value, Region } from "@contracts/client";
import { PresentedValue } from "../../components/PresentedValue";
import { Entries } from "../../ui/regions/Entries";
import { ChipList, chipValues } from "../../ui/ChipList";

export const CaseIdentity = ({
  region,
  attention,
  timeZone,
  requestedReference,
  renderIdentity,
}: {
  region: Region | undefined;
  attention: Region | undefined;
  timeZone: string;
  requestedReference: string | undefined;
  renderIdentity?: (heading: ReactNode, facts: ReactNode) => ReactNode;
}) => {
  const entries = region?.content.kind === "ENTRIES" ? region.content.entries : [];
  const reference = entries.find((entry) => entry.value.kind === "REFERENCE");
  const headline = entries.flatMap(({ value }) => (value.kind === "STATE" ? [value] : []));
  const reasons = attention?.content.kind === "CODES" ? chipValues(attention.content) : [];
  const facts = entries.filter(
    (entry) =>
      entry !== reference &&
      entry.prominence === "PRIMARY" &&
      !["STATE", "CODE", "CODES", "ABSENT"].includes(entry.value.kind),
  );
  let title: Value = { kind: "MESSAGE", messageId: "label.caseDetail" };
  if (reference !== undefined) {
    title = reference.value;
  } else if (requestedReference !== undefined) {
    title = { kind: "REFERENCE", value: requestedReference };
  }
  const heading = (
    <div className="case-title-line">
      <h1 id="case-detail-title" tabIndex={-1}>
        <PresentedValue value={title} timeZone={timeZone} />
      </h1>
      <div className="case-header-state">
        <ChipList values={[...headline, ...reasons]} />
      </div>
    </div>
  );
  const factsView =
    facts.length === 0 ? null : (
      <div className="case-identity-facts">
        <Entries entries={facts} timeZone={timeZone} />
      </div>
    );
  return renderIdentity === undefined ? (
    <div className="case-identity" data-region="IDENTITY">
      {heading}
      {factsView}
    </div>
  ) : (
    renderIdentity(heading, factsView)
  );
};
