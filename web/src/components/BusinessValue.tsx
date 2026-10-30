import { renderIsolatedValue } from "../presentation/messages";
import type { FieldCatalogEntry } from "../api/service";
import { usePresentation } from "../presentation/context";
import { suspiciousCodePoints } from "../utils/text";

export const CharacterWarning = ({ value }: { value: string | null }) => {
  const p = usePresentation();
  const points = value === null ? [] : suspiciousCodePoints(value);
  return points.length === 0 ? null : (
    <small className="character-warning">
      {p.text("label.characterWarning", { characters: points.join(", ") })}
    </small>
  );
};

const valueDirection = (field: FieldCatalogEntry): "ltr" | "auto" =>
  field.name === "caseReference" ||
  field.scalar.kind === "AMOUNT" ||
  field.scalar.kind === "CALENDAR_DATE"
    ? "ltr"
    : "auto";
export const BusinessValue = ({
  value,
  field,
  context,
  currency,
}: {
  value: string | null;
  field: FieldCatalogEntry;
  context?: "ui.before" | "ui.after";
  currency?: string;
}) => {
  const p = usePresentation();
  const display =
    value !== null && field.scalar.kind === "AMOUNT" && currency !== undefined
      ? p.amount(value, currency)
      : p.fieldValue(value, field);
  return (
    <span>
      {context === undefined ? (
        <bdi
          dir={valueDirection(field)}
          className={field.name === "caseReference" ? "identifier-value" : undefined}
        >
          {display}
        </bdi>
      ) : (
        renderIsolatedValue(
          p,
          context,
          { value: display },
          <bdi
            key="value"
            dir={valueDirection(field)}
            className={field.name === "caseReference" ? "identifier-value" : undefined}
          >
            {display}
          </bdi>,
          "value",
        )
      )}
      <CharacterWarning value={value} />
    </span>
  );
};
