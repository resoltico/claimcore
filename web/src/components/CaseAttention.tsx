import type { CaseSummary } from "@contracts/client";
import { usePresentation } from "../presentation/context";
import "./case-indicators.css";
/** Preserve the core's attention flags and order, including an explicit empty result. */
export const CaseAttention = ({ values }: { values: CaseSummary["attention"] }) => {
  const p = usePresentation();
  return values.length === 0 ? null : (
    <ul className="case-attention">
      {values.map((value) => (
        <li key={value}>
          <span className="badge" data-attention={value}>
            {p.term("attention", value)}
          </span>
        </li>
      ))}
    </ul>
  );
};
