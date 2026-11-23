import type { RegionContent } from "@contracts/client";
import { Chip, type ChipValue } from "./Chip";
import "./chip-list.css";

export const chipValues = (
  content: Extract<RegionContent, { kind: "CODES" }>,
): ReadonlyArray<ChipValue> =>
  content.tokens.map((token) => ({ vocabulary: content.vocabulary, token }));

export const ChipList = ({ values }: { values: ReadonlyArray<ChipValue> }) =>
  values.length === 0 ? null : (
    <ul className="chip-list">
      {values.map((value) => (
        <li key={`${value.vocabulary}:${value.token}`}>
          <Chip value={value} />
        </li>
      ))}
    </ul>
  );
