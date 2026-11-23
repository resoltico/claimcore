import { usePresentation } from "../presentation/context";
import { suspiciousCodePoints } from "../utils/text";

export const CharacterWarning = ({
  value,
  allowLineFeed = false,
}: {
  value: string | null;
  allowLineFeed?: boolean;
}) => {
  const p = usePresentation();
  const points =
    value === null ? [] : suspiciousCodePoints(allowLineFeed ? value.replaceAll("\n", "") : value);
  return points.length === 0 ? null : (
    <span className="character-warning">
      {p.text("label.characterWarning", { characters: points.join(", ") })}
    </span>
  );
};
