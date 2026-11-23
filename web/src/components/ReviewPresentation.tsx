import type { PresentedChange, ReviewPresentation as Presentation } from "@contracts/client";
import { usePresentation } from "../presentation/context";
import { renderCatalogKey } from "../presentation/messages";
import { PresentedValue } from "./PresentedValue";

const identifier = (entry: PresentedChange) =>
  entry.before.kind === "IDENTIFIER" || entry.after.kind === "IDENTIFIER";

/** The supplied order and before/after values are authoritative, including explicit absence. */
export const ReviewPresentation = ({
  value,
  timeZone,
  technical = false,
  creation = false,
}: {
  value: Presentation;
  timeZone: string;
  technical?: boolean;
  creation?: boolean;
}) => {
  const p = usePresentation();
  return (
    <dl className="review-values">
      {value.changes
        .filter((entry) => identifier(entry) === technical)
        .map((entry) => (
          <div key={entry.key}>
            <dt>{renderCatalogKey(p, entry.labelId)}</dt>
            {creation ? (
              <dd>
                <PresentedValue value={entry.after} timeZone={timeZone} />
              </dd>
            ) : (
              <dd className="review-comparison">
                <div className="review-before">
                  <span className="field-hint">{p.text("label.before")}</span>
                  <PresentedValue value={entry.before} timeZone={timeZone} />
                </div>
                <span className="review-arrow" aria-hidden="true">
                  →
                </span>
                <div className="review-after">
                  <span className="field-hint">{p.text("label.after")}</span>
                  <PresentedValue value={entry.after} timeZone={timeZone} />
                </div>
              </dd>
            )}
          </div>
        ))}
    </dl>
  );
};
