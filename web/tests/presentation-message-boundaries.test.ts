import { expect, it } from "vitest";
import { renderKey, renderReference } from "../src/presentation/messages";

it("renders an empty attempt page as zero attempts rather than an unknown diagnostic [CC-WEB-001]", () => {
  expect(
    renderKey({ language: "en", displayLocale: "en-GB" }, "ui.attemptCount", { count: 0 }),
  ).toBe("0 attempts shown.");
  expect(
    renderKey({ language: "ar", displayLocale: "en-GB" }, "ui.attemptCount", { count: 0 }),
  ).toBe("أدلة المحاولات: لا شيء معروض.");
});
it("isolates technical revision holes in Arabic reference summaries and preserves literal word order [CC-WEB-001]", () => {
  const reference = { exact: "A\u2069\u202E العربية" };
  const values = { reference: reference.exact, revision: "12" };
  expect(
    renderReference(
      { language: "en", displayLocale: "en-GB" },
      "ui.reviewTarget",
      values,
      reference,
    ),
  ).toEqual(["Target ", reference, " · expected revision 12"]);
  expect(
    renderReference(
      { language: "ar", displayLocale: "en-GB" },
      "ui.reviewTarget",
      values,
      reference,
    ),
  ).toEqual(["الهدف ", reference, " · المراجعة المتوقعة \u206812\u2069"]);
});
