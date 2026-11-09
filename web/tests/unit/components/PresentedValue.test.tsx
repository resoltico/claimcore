import { expect, it } from "vitest";
import { render, screen } from "../../support/presentation-test-support";
import { PresentedValue } from "../../../src/components/PresentedValue";
import { preferenceKey } from "../../../src/presentation/preferences";

it.each([
  ["en-GB", "1.0000 EUR"],
  ["lv-LV", "1,0000 EUR"],
  ["ar-EG", "١٫٠٠٠٠ EUR"],
] as const)("preserves supplied four-place money scale in %s", (displayLocale, expected) => {
  localStorage.setItem(
    preferenceKey,
    JSON.stringify({ version: 3, language: "en", displayLocale, mode: "light", accent: "green" }),
  );
  render(
    <PresentedValue
      value={{ kind: "MONEY", amount: "1", currency: "EUR", displayScale: 4 }}
      timeZone="Etc/UTC"
    />,
  );
  expect(screen.getByText(expected, { exact: true })).toBeVisible();
});
it("uses supplied scale zero instead of currency padding and never rounds exact fractional text", () => {
  const view = render(
    <PresentedValue
      value={{ kind: "MONEY", amount: "1", currency: "EUR", displayScale: 0 }}
      timeZone="Etc/UTC"
    />,
  );
  expect(view.container).toHaveTextContent("1 EUR");
  view.rerender(
    <PresentedValue
      value={{
        kind: "MONEY_TOTAL",
        amount: "9999999999999999999999999999999999999999.1234",
        currency: "EUR",
        displayScale: 0,
      }}
      timeZone="Etc/UTC"
    />,
  );
  expect(view.container).toHaveTextContent(
    "9,999,999,999,999,999,999,999,999,999,999,999,999,999.1234 EUR",
  );
});

it("formats declared codes while retaining literal text, whitespace and technical identifier direction", () => {
  const identifier = "10000000-0000-4000-8000-000000000001";
  const view = render(
    <>
      <PresentedValue
        value={{ kind: "CODE", vocabulary: "principalKind", token: "HUMAN" }}
        timeZone="UTC"
      />
      <PresentedValue value={{ kind: "TEXT", value: "HUMAN" }} timeZone="UTC" />
      <PresentedValue value={{ kind: "TEXT", value: " " }} timeZone="UTC" />
      <PresentedValue value={{ kind: "IDENTIFIER", value: identifier }} timeZone="UTC" />
    </>,
  );
  expect(screen.getByText("Person", { exact: true })).toBeVisible();
  expect(screen.getByText("HUMAN", { exact: true })).toBeVisible();
  expect(screen.getByText(identifier, { exact: true })).toHaveAttribute("dir", "ltr");
  expect([...view.container.querySelectorAll("bdi")].some((node) => node.textContent === " ")).toBe(
    true,
  );
});
