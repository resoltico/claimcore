import { expect } from "vitest";
import type { OperationInput } from "@contracts/client";

type Group = "registration" | "decision" | "payment";
type Literal = { act: { groups: Record<Group, { values?: Record<string, unknown> }> } };

// Keep every literal oracle value. Separately require complete authored patch identity with the authored
// input, so unmentioned fields cannot disappear or acquire extra values during serialization.
export const expectCorrection = (
  actual: OperationInput,
  serializedLiteral: string,
  authored: Record<Group, object>,
) => {
  if (actual.kind !== "CASE" || actual.act.kind !== "CORRECT") {
    throw new Error("Expected a case correction.");
  }
  const literal = JSON.parse(serializedLiteral) as Literal;
  const projected: Record<string, unknown> = { ...actual.act.groups };
  for (const group of ["registration", "decision", "payment"] as const) {
    const actualGroup = actual.act.groups[group];
    const literalValues = literal.act.groups[group].values;
    if ("values" in actualGroup && literalValues !== undefined) {
      expect(actualGroup.values).toEqual(authored[group]);
      const keys = new Set(Object.keys(literalValues));
      projected[group] = {
        ...actualGroup,
        values: Object.fromEntries(
          Object.entries(actualGroup.values).filter(([key]) => keys.has(key)),
        ),
      };
    }
  }
  expect({ ...actual, act: { ...actual.act, groups: projected } }).toEqual({
    kind: "CASE",
    ...literal,
  });
};
