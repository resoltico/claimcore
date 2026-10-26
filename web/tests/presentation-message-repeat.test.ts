import { expect, it } from "vitest";
import { translate } from "../src/presentation/messages";
import { defaults } from "../src/presentation/preferences";

it("repeated rendering retains each request's values and selected language", () => {
  expect(translate(defaults, "notice.accepted", { operationId: "one" })).toBe(
    "Accepted exact operation one.",
  );
  expect(translate(defaults, "notice.accepted", { operationId: "two" })).toBe(
    "Accepted exact operation two.",
  );
  expect(
    translate({ ...defaults, language: "lv" }, "notice.accepted", { operationId: "one" }),
  ).toBe("Pieņemta precīzā operācija one.");
  expect(translate(defaults, "notice.accepted", { operationId: "three" })).toBe(
    "Accepted exact operation three.",
  );
});
