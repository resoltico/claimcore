import type { Terms } from "@contracts/messages";
import { usePresentation } from "../presentation/context";
import "../ui/regions/codes.css";
/** The core selects the headline; the renderer only formats its closed vocabulary. */
export const StateChip = ({ value }: { value: Terms["headline"] }) => {
  const p = usePresentation();
  return (
    <span className="badge case-headline" data-headline={value}>
      {p.term("headline", value)}
    </span>
  );
};
