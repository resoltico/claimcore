import { expect, it } from "vitest";
import {
  TYPE,
  type LiteralElement,
  type MessageFormatElement,
} from "@formatjs/icu-messageformat-parser";
import { renderKey, renderIsolatedValue } from "../src/presentation/messages";
import { pseudolocalize } from "../src/presentation/pseudolocalization.mjs";
import pseudo from "../src/presentation/generated/en-XA.json";

const literal = (value: string): LiteralElement => ({ type: TYPE.literal, value });

it("derives the published pseudo label exactly from the preserved generated catalog [CC-WEB-001]", () => {
  expect(
    renderKey({ language: "en-XA", displayLocale: "en-GB" }, "field.caseReference.label"),
  ).toBe(pseudo["field.caseReference.label"].map((node) => node.value).join(""));
});

it("pseudolocalizes nested literals while preserving argument, selector and plural identities [CC-WEB-001]", () => {
  const ast: MessageFormatElement[] = [
    literal("aeiouAEIOU "),
    { type: TYPE.argument, value: "label" },
    {
      type: TYPE.plural,
      value: "count",
      offset: 1,
      pluralType: "cardinal",
      options: {
        one: { value: [literal("One "), { type: TYPE.pound }] },
        other: { value: [literal("Other "), { type: TYPE.pound }] },
      },
    },
    {
      type: TYPE.select,
      value: "kind",
      options: { OPEN: { value: [literal("Open")] }, other: { value: [literal("Unknown")] } },
    },
  ];
  const before = JSON.stringify(ast);
  const transformed = pseudolocalize(ast);
  expect(transformed).toEqual([
    literal("ààëëïïööüüÀÀËËÏÏÖÖÜÜ "),
    { type: TYPE.argument, value: "label" },
    {
      type: TYPE.plural,
      value: "count",
      offset: 1,
      pluralType: "cardinal",
      options: {
        one: { value: [literal("ÖÖnëë "), { type: TYPE.pound }] },
        other: { value: [literal("ÖÖthëër "), { type: TYPE.pound }] },
      },
    },
    {
      type: TYPE.select,
      value: "kind",
      options: { OPEN: { value: [literal("ÖÖpëën")] }, other: { value: [literal("ÜÜnknööwn")] } },
    },
  ]);
  expect(JSON.stringify(ast)).toBe(before);
  expect(transformed).not.toBe(ast);
});

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
    renderIsolatedValue(
      { language: "en", displayLocale: "en-GB" },
      "ui.reviewTarget",
      values,
      reference,
    ),
  ).toEqual(["Target ", reference, " · expected revision 12"]);
  expect(
    renderIsolatedValue(
      { language: "ar", displayLocale: "en-GB" },
      "ui.reviewTarget",
      values,
      reference,
    ),
  ).toEqual(["الهدف ", reference, " · المراجعة المتوقعة \u206812\u2069"]);
});
