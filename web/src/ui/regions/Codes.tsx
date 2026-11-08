import type { RegionContent } from "@contracts/client";
import { usePresentation } from "../../presentation/context";
import "./codes.css";
export const Codes = ({ content }: { content: Extract<RegionContent, { kind: "CODES" }> }) => {
  const p = usePresentation();
  return content.tokens.length === 0 ? null : (
    <ul className="region-codes" data-vocabulary={content.vocabulary}>
      {content.tokens.map((token) => (
        <li key={token}>
          <span className="badge" data-code={token}>
            {p.term(content.vocabulary, token)}
          </span>
        </li>
      ))}
    </ul>
  );
};
