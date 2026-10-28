import type { Language } from "../../src/presentation/preferences";
import { expect, it } from "vitest";
import { renderKey, renderCatalogKey, renderIsolatedValue } from "../../src/presentation/messages";
import { templateParts, pseudolocalizeLiteral } from "../../src/presentation/templates";
import vectors from "../../../tests/fixtures/message-template-vectors.json";

it("matches independent quoting, isolation, RTL and pseudolocale vectors", () => {
  for (const row of vectors.accepted) {
    const supplied: Readonly<Record<string, string>> = row.arguments;
    expect(
      templateParts(row.source)
        .map((part) => (part.kind === "literal" ? part.value : supplied[part.value]))
        .join(""),
    ).toBe(row.expected);
  }
  for (const row of vectors.presentation) {
    expect(
      renderCatalogKey(
        { language: row.language as Language, displayLocale: "en-GB" },
        row.key,
        row.arguments,
      ),
    ).toBe(row.expected);
  }
});

it("pseudolocalizes only literal text while preserving authored argument values", () => {
  expect(pseudolocalizeLiteral("aeiouAEIOU")).toBe("ààëëïïööüüÀÀËËÏÏÖÖÜÜ");
  expect(
    renderKey({ language: "en-XA", displayLocale: "en-GB" }, "ui.reference", {
      label: "EXACT-A",
      reference: "EXACT-B",
    }),
  ).toBe("⟦EXACT-A: EXACT-B⟧");
});

it("isolates technical revision holes in Arabic reference summaries and preserves literal word order", () => {
  const reference = { exact: "A\u2069\u202E العربية" };
  const values = { status: reference.exact, revision: "12" };
  expect(
    renderIsolatedValue(
      { language: "en", displayLocale: "en-GB" },
      "ui.caseRow",
      values,
      reference,
      "status",
    ),
  ).toEqual([reference, " · revision 12"]);
  expect(
    renderIsolatedValue(
      { language: "ar", displayLocale: "en-GB" },
      "ui.caseRow",
      values,
      reference,
      "status",
    ),
  ).toEqual([reference, " · المراجعة \u206812\u2069"]);
});
