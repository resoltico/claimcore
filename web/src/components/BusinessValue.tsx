import type { FieldDescriptor } from "../api/v3";
import { usePresentation } from "../presentation/context";
import { suspiciousCodePoints } from "../utils/text";

export const CharacterWarning = ({ value }: { value: string | null }) => {
  const p = usePresentation();
  const points = value === null ? [] : suspiciousCodePoints(value);
  return points.length === 0 ? null : (
    <small className="character-warning">
      {p.text("ui.characterWarning", { characters: points.join(", ") })}
    </small>
  );
};

export const BusinessValue = ({
  value,
  field,
  context,
}: {
  value: string | null;
  field: FieldDescriptor;
  context?: "ui.before" | "ui.after";
}) => {
  const p = usePresentation();
  const display = p.fieldValue(value, field);
  return (
    <span>
      <bdi>{context === undefined ? display : p.text(context, { value: display })}</bdi>
      <CharacterWarning value={value} />
    </span>
  );
};
