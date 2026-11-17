import { expect, it } from "vitest";
import { inputChoiceAllowed } from "../../../src/operation/inputChoices";

it("intersects all matching supplied constraints and leaves unmatched choices alone", () => {
  const input = {
    choicesWhen: [
      { input: "scope", value: "selected", choices: ["first", "shared"] },
      { input: "principal", value: "restricted", choices: ["last", "shared"] },
    ],
  };
  expect(inputChoiceAllowed(input, "shared", { scope: "selected", principal: "restricted" })).toBe(
    true,
  );
  expect(inputChoiceAllowed(input, "first", { scope: "selected", principal: "restricted" })).toBe(
    false,
  );
  expect(inputChoiceAllowed(input, "last", { scope: "other", principal: "restricted" })).toBe(true);
  expect(inputChoiceAllowed(input, "arbitrary", {})).toBe(true);
  expect(inputChoiceAllowed({ choicesWhen: [] }, "stored", { scope: "selected" })).toBe(true);
});
