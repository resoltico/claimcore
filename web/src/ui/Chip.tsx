import { codeTones, type PresentedCode, type PresentedScalar_STATE } from "@contracts/client";
import { usePresentation } from "../presentation/context";
import "./chip.css";

export type ChipValue = Pick<PresentedCode | PresentedScalar_STATE, "vocabulary" | "token"> &
  Pick<PresentedScalar_STATE, "detail">;

export const chipTone = ({ vocabulary, token }: ChipValue): string | undefined => {
  const tones: Readonly<Record<string, Readonly<Record<string, string>>>> = codeTones;
  return tones[vocabulary]?.[token];
};

export const Chip = ({ value }: { value: ChipValue }) => {
  const p = usePresentation();
  const state = p.term(value.vocabulary, value.token);
  return (
    <span className="chip" data-tone={(chipTone(value) ?? "NEUTRAL").toLowerCase()}>
      {value.detail === undefined
        ? state
        : p.text("headline.withDetail", {
            state,
            detail: p.term(value.detail.vocabulary, value.detail.token),
          })}
    </span>
  );
};
