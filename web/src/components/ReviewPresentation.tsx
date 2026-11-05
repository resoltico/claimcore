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
}: {
  value: Presentation;
  timeZone: string;
  technical?: boolean;
}) => {
  const p = usePresentation();
  return (
    <dl className="review-values">
      {value.changes
        .filter((entry) => identifier(entry) === technical)
        .map((entry) => (
          <div key={entry.key}>
            <dt>{renderCatalogKey(p, entry.labelId)}</dt>
            <dd className="review-comparison">
              <div className="review-before">
                <small>{p.text("label.before")}</small>
                <PresentedValue value={entry.before} timeZone={timeZone} />
              </div>
              <span className="review-arrow" aria-hidden="true">
                →
              </span>
              <div className="review-after">
                <small>{p.text("label.after")}</small>
                <PresentedValue value={entry.after} timeZone={timeZone} />
              </div>
            </dd>
          </div>
        ))}
    </dl>
  );
};

export const UnchangedReview = ({ value }: { value: Presentation }) => {
  const p = usePresentation();
  if (value.unchanged.length === 0) {
    return null;
  }
  const labels = value.unchanged.map((entry) => renderCatalogKey(p, entry.labelId));
  return (
    <p className="review-unchanged">
      <strong>{p.text("label.unchanged")}:</strong>{" "}
      {new Intl.ListFormat(p.language === "en-XA" ? "en" : p.language).format(labels)}
    </p>
  );
};
