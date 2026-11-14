import { expect } from "vitest";
import type { OperationSubmission } from "@contracts/client";
import { prefixedInputs } from "./submission-inputs";
type Group = "registration" | "decision";
/** Literal values stay independent while full authored patches cannot silently lose fields. */
export const expectCorrection = (
  actual: OperationSubmission,
  serializedLiteral: string,
  authored: Record<Group, object>,
) => {
  if (actual.task !== "case.correct") {
    throw new Error("Expected a case correction.");
  }
  const literal = JSON.parse(serializedLiteral) as Extract<
    OperationSubmission,
    { task: "case.correct" }
  >;
  const expected: Record<string, unknown> = { ...literal.inputs };
  for (const group of ["registration", "decision"] as const) {
    if (literal.inputs[`group:${group}`] === "UPDATE") {
      Object.assign(expected, prefixedInputs("field:", authored[group]));
    }
  }
  expect(actual.inputs).toEqual(expected);
  for (const [key, value] of Object.entries(literal.inputs)) {
    expect(actual.inputs).toHaveProperty(key, value);
  }
  expect({ ...actual, inputs: literal.inputs }).toEqual(literal);
};
