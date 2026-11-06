import type { Language } from "../../src/presentation/preferences";
import { expect, it } from "vitest";
import {
  renderKey,
  renderCatalogKey,
  renderIsolatedValue,
  renderIsolatedValues,
} from "../../src/presentation/messages";
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
        {
          language: row.language as Language,
          displayLocale: "en-GB",
          mode: "system" as const,
          accent: "green" as const,
        },
        row.key,
        row.arguments,
      ),
    ).toBe(row.expected);
  }
});

it("pseudolocalizes only literal text while preserving authored argument values", () => {
  expect(pseudolocalizeLiteral("aeiouAEIOU")).toBe("ààëëïïööüüÀÀËËÏÏÖÖÜÜ");
  expect(
    renderKey(
      {
        language: "en-XA",
        displayLocale: "en-GB",
        mode: "system" as const,
        accent: "green" as const,
      },
      "ui.reference",
      {
        label: "EXACT-A",
        reference: "EXACT-B",
      },
    ),
  ).toBe("⟦EXACT-A: EXACT-B⟧");
});

it("isolates the other reference hole in Arabic and preserves literal word order", () => {
  const reference = { exact: "A\u2069\u202E العربية" };
  const values = { label: reference.exact, reference: "12" };
  expect(
    renderIsolatedValue(
      { language: "en", displayLocale: "en-GB", mode: "system", accent: "green" },
      "ui.reference",
      values,
      reference,
      "label",
    ),
  ).toEqual([reference, ": 12"]);
  expect(
    renderIsolatedValue(
      { language: "ar", displayLocale: "en-GB", mode: "system", accent: "green" },
      "ui.reference",
      values,
      reference,
      "label",
    ),
  ).toEqual([reference, ": \u206812\u2069"]);
});

it("preserves both isolated argument nodes in translated sentence order", () => {
  const label = { value: "Mixed العربية" };
  const reference = { value: "A\u2069\u202E" };
  for (const language of ["en", "ar"] as const) {
    expect(
      renderIsolatedValues(
        { language, displayLocale: "en-GB", mode: "system", accent: "green" },
        "ui.reference",
        { label: label.value, reference: reference.value },
        { label, reference },
      ),
    ).toEqual([label, ": ", reference]);
  }
});
