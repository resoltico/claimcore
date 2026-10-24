import { expect, it } from "vitest";
import {
  TYPE,
  parse,
  type LiteralElement,
  type MessageFormatElement,
} from "@formatjs/icu-messageformat-parser";
import { renderKey, renderIsolatedValue } from "../src/presentation/messages";
import { pseudolocalize } from "../src/presentation/pseudolocalization.mjs";
import english from "../../src/ClaimCore.Contract/Localization/en.field.json";

const literal = (value: string): LiteralElement => ({ type: TYPE.literal, value });

it("derives the published pseudo label exactly from the authored source catalog [CC-WEB-001]", () => {
  expect(
    renderKey({ language: "en-XA", displayLocale: "en-GB" }, "field.caseReference.label"),
  ).toBe(
    `⟦${pseudolocalize(parse(english["field.caseReference.label"]))
      .map((node) => (node.type === TYPE.literal ? node.value : ""))
      .join("")}⟧`,
  );
});

it("pseudolocalizes literal nodes while preserving exact argument identities [CC-WEB-001]", () => {
  const ast: MessageFormatElement[] = [
    literal("aeiouAEIOU "),
    { type: TYPE.argument, value: "label" },
  ];
  const before = JSON.stringify(ast);
  const transformed = pseudolocalize(ast);
  expect(transformed).toEqual([
    literal("ààëëïïööüüÀÀËËÏÏÖÖÜÜ "),
    { type: TYPE.argument, value: "label" },
  ]);
  expect(JSON.stringify(ast)).toBe(before);
  expect(transformed).not.toBe(ast);
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
