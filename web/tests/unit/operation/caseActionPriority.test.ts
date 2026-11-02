import { expect, it } from "vitest";
import { casePrimaryAction } from "../../../src/operation/caseActionPriority";
import { current } from "../../support/operation-form.fixtures";

it.each(["UNDECIDED", "DECIDED", "PAID"] as const)(
  "ranks %s explicitly regardless of offer order",
  (progress) => {
    const acts = [
      "ERASE",
      "REINSTATE",
      "VOID",
      "UPDATE_WORK",
      "AMEND_REGISTRATION",
      "DECIDE",
      "RECORD_PAYMENT",
      "CLOSE",
    ] as const;
    const expected = { UNDECIDED: "DECIDE", DECIDED: "RECORD_PAYMENT", PAID: "CLOSE" };
    for (const offered of [acts, [...acts].reverse(), [...acts.slice(3), ...acts.slice(0, 3)]]) {
      expect(
        casePrimaryAction({
          ...current,
          state: { ...current.state, fields: { ...current.state.fields, progress } },
          advice: { ...current.advice, acts: offered },
        }),
      ).toBe(expected[progress]);
    }
  },
);
it("has no implicit reopening, reinstatement or erasure default", () => {
  const advice = { ...current.advice, acts: ["ERASE", "REINSTATE", "REOPEN", "VOID"] as const };
  expect(casePrimaryAction({ ...current, advice })).toBeUndefined();
  expect(
    casePrimaryAction({
      ...current,
      advice,
      state: { ...current.state, fields: { ...current.state.fields, status: "CLOSED" } },
    }),
  ).toBeUndefined();
  expect(
    casePrimaryAction({ ...current, advice, state: { ...current.state, kind: "VOIDED" } }),
  ).toBeUndefined();
});
