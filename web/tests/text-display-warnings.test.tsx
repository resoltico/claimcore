import { expect, it } from "vitest";
import { BusinessValue, CharacterWarning } from "../src/components/BusinessValue";
import { hasSuspiciousCharacters, suspiciousCodePoints } from "../src/utils/text";
import { preferenceKey } from "../src/presentation/preferences";
import { render } from "./presentation-test-support";
import { definition } from "./service-ui.fixtures";

it.each([
  { value: "\u0000", code: "U+0" },
  { value: "\u0085", code: "U+85" },
  { value: "\u180E", code: "U+180E" },
  { value: "\u200D", code: "U+200D" },
  { value: "\u2028", code: "U+2028" },
  { value: "\u2029", code: "U+2029" },
  { value: "\u{E0001}", code: "U+E0001" },
  { value: "\u{E0020}", code: "U+E0020" },
])("warns about the independent display scalar $code", ({ value, code }) => {
  expect(suspiciousCodePoints(value)).toEqual([code]);
  expect(hasSuspiciousCharacters(value)).toBe(true);
});
it.each([
  { name: "empty text", value: "" },
  { name: "plain ASCII", value: "plain" },
  { name: "decomposed combining text", value: "A\u0308" },
  { name: "BMP private use", value: "\uE000" },
  { name: "supplementary private use", value: "\u{F0000}" },
  { name: "plain emoji", value: "🙂🚀" },
  { name: "emoji variation selector", value: "✈\uFE0F" },
])("does not classify $name as a warning", ({ value }) => {
  expect(suspiciousCodePoints(value)).toEqual([]);
  expect(hasSuspiciousCharacters(value)).toBe(false);
});

const eight = "\u0000\t\n\u0085\u00AD\u180E\u200D\u2028";
const eightCodes = ["U+0", "U+9", "U+A", "U+85", "U+AD", "U+180E", "U+200D", "U+2028"];
it("deduplicates warning scalars in first-seen order and counts distinct scalars for the bound", () => {
  expect(suspiciousCodePoints("\u180E\u180E\u{E0001}\u180E\u200D\u{E0001}")).toEqual([
    "U+180E",
    "U+E0001",
    "U+200D",
  ]);
  expect(suspiciousCodePoints(`${eight}${eight}\u180E`)).toEqual(eightCodes);
});
it("shows eight distinct scalars without ellipsis and marks the ninth without including it", () => {
  expect(suspiciousCodePoints(eight)).toEqual(eightCodes);
  expect(suspiciousCodePoints(`${eight}\u2029\u{E0001}`)).toEqual([...eightCodes, "…"]);
});

const languages = [
  { language: "en", prefix: "Contains ", open: "", close: "" },
  { language: "lv", prefix: "Satur ", open: "", close: "" },
  { language: "ar", prefix: "يحتوي على ", open: "\u2068", close: "\u2069" },
] as const;
const preferences = (language: (typeof languages)[number]["language"]) => {
  localStorage.setItem(
    preferenceKey,
    JSON.stringify({ version: 1, language, displayLocale: "en-GB" }),
  );
};
it.each(languages)(
  "preserves exact business text and localizes supplementary scalar warnings in $language",
  ({ language, prefix, open, close }) => {
    preferences(language);
    const field = definition.catalog.fields.find((item) => item.name === "claimantName")!;
    const value = "A\u0308 العربية\u180E\u200D\u{E0001}\u{E0020} <script>🙂\uE000";
    const plain = "A\u0308 🙂 \uE000\u{F0000}";
    const view = render(
      <>
        <BusinessValue value={value} field={field} />
        <BusinessValue value={plain} field={field} />
        <CharacterWarning value={null} />
      </>,
    );
    expect([...view.container.querySelectorAll("bdi")].map((node) => node.textContent)).toEqual([
      value,
      plain,
    ]);
    const warnings = view.container.querySelectorAll(".character-warning");
    expect(warnings).toHaveLength(1);
    expect(warnings[0]!.textContent).toBe(
      `${prefix}${open}U+180E, U+200D, U+E0001, U+E0020${close}`,
    );
    expect(view.container.querySelector("script")).toBeNull();
  },
);
it.each(languages)(
  "renders the bounded warning and ellipsis in $language",
  ({ language, prefix, open, close }) => {
    preferences(language);
    const view = render(<CharacterWarning value={`${eight}\u2029`} />);
    expect(view.container.querySelector(".character-warning")!.textContent).toBe(
      `${prefix}${open}U+0, U+9, U+A, U+85, U+AD, U+180E, U+200D, U+2028, …${close}`,
    );
  },
);
