import type { CaseSummary } from "@contracts/client";
import { usePresentation } from "../presentation/context";
import "./case-indicators.css";
/** The core selects the headline; the renderer only formats its closed vocabulary. */
export const CaseHeadline = ({ value }: { value: CaseSummary["headline"] }) => {
  const p = usePresentation();
  return (
    <span className="badge case-headline" data-headline={value}>
      {p.term("headline", value)}
    </span>
  );
};
