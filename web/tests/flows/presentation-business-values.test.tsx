import { expect, it } from "vitest";
import { BusinessValue, CharacterWarning } from "../../src/components/BusinessValue";
import { preferenceKey } from "../../src/presentation/preferences";
import { render, screen } from "../support/presentation-test-support";
import { definition } from "../support/service-ui.fixtures";

it("formats review values by catalog definition with independent date and amount expectations", () => {
  localStorage.setItem(
    preferenceKey,
    JSON.stringify({
      version: 3,
      language: "en",
      displayLocale: "lv-LV",
      mode: "system" as const,
      accent: "green" as const,
    }),
  );
  const field = (name: string) => definition.catalog.fields.find((item) => item.name === name)!;
  render(
    <>
      <BusinessValue
        value="999999999999999999.0100"
        field={field("claimedAmount")}
        context="ui.before"
      />
      <BusinessValue value="0099-12-31" field={field("incidentDate")} context="ui.after" />
      <BusinessValue value={null} field={field("paymentDate")} context="ui.before" />
      <BusinessValue value="EUR" field={field("claimedCurrency")} />
    </>,
  );
  expect(document.querySelector("bdi")?.textContent).toBe("999 999 999 999 999 999,0100");
  expect(screen.getByText("31.12.0099")).toBeVisible();
  expect(screen.getByText("Not recorded")).toBeVisible();
  expect(screen.getByText("EUR").tagName).toBe("BDI");
});

it("isolates and warns about permitted business Unicode without normalizing or executing markup", () => {
  const field = definition.catalog.fields.find((item) => item.name === "claimantName")!;
  const value = "A\u0308 <script> العربية\u200D\u202E";
  render(
    <>
      <BusinessValue value={value} field={field} context="ui.after" />
      <CharacterWarning value={null} />
    </>,
  );
  expect(screen.getByText(value).tagName).toBe("BDI");
  expect(screen.getByText("Contains U+200D, U+202E")).toBeVisible();
  expect(document.querySelector("script")).toBeNull();
});
