import { PageTitle } from "../../ui/Page";
import { usePresentation } from "../../presentation/context";
import { useLayoutEffect, useRef, useState, type ReactNode } from "react";
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
  const p = usePresentation();
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
      {renderIdentity === undefined ? (
        <h2 className="case-context-title">
          <PresentedValue value={title} timeZone={timeZone} />
        </h2>
      ) : (
        <PageTitle
          id="case-detail-title"
          title={title.kind === "REFERENCE" ? title.value : p.text("label.caseDetail")}
        >
          <PresentedValue value={title} timeZone={timeZone} />
        </PageTitle>
      )}
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

export const CaseTitleStrip = ({ children }: { children: ReactNode }) => {
  const title = useRef<HTMLDivElement>(null);
  const [stuck, setStuck] = useState(false);
  useLayoutEffect(() => {
    const element = title.current;
    const card = element?.parentElement;
    if (element === null || card === null || card === undefined) {
      return;
    }
    const observe = () => {
      const edge = card.getBoundingClientRect().top + card.clientTop;
      setStuck(element.getBoundingClientRect().top > edge + 1);
    };
    observe();
    window.addEventListener("scroll", observe, { passive: true });
    window.addEventListener("resize", observe);
    return () => {
      window.removeEventListener("scroll", observe);
      window.removeEventListener("resize", observe);
    };
  }, []);
  return (
    <div
      ref={title}
      className="case-title-sticky"
      data-region="IDENTITY"
      data-stuck={stuck || undefined}
    >
      {children}
    </div>
  );
};
