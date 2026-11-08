import { expect, it } from "vitest";
import { translate } from "../../src/presentation/messages";
import { defaults } from "../../src/presentation/preferences";

it("repeated rendering retains each request's values and selected language", () => {
  expect(translate(defaults, "notice.accepted", { operationId: "one" })).toBe(
    "Change recorded (one).",
  );
  expect(translate(defaults, "notice.accepted", { operationId: "two" })).toBe(
    "Change recorded (two).",
  );
  expect(
    translate({ ...defaults, language: "lv" }, "notice.accepted", { operationId: "one" }),
  ).toBe("Izmaiņa reģistrēta (one).");
  expect(translate(defaults, "notice.accepted", { operationId: "three" })).toBe(
    "Change recorded (three).",
  );
});
