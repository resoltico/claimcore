import { expect, it } from "vitest";
import { BusinessValue, CharacterWarning } from "../src/components/BusinessValue";
import { preferenceKey } from "../src/presentation/preferences";
import { createPresenter } from "../src/presentation/presenter";
import { render, screen } from "./presentation-test-support";
import { definition } from "./v3-ui.fixtures";

it("formats review values by descriptor with independent date and amount expectations [CC-DOM-001]", () => {
  localStorage.setItem(
    preferenceKey,
    JSON.stringify({ version: 1, language: "en", displayLocale: "lv-LV" }),
  );
  const field = (name: string) => definition.definition.fields.find((item) => item.name === name)!;
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
  expect(document.querySelector("bdi")?.textContent).toBe("Before: 999 999 999 999 999 999,0100");
  expect(screen.getByText("After: 31.12.0099")).toBeVisible();
  expect(screen.getByText("Before: Not recorded")).toBeVisible();
  expect(screen.getByText("EUR").tagName).toBe("BDI");
});

it("isolates and warns about permitted business Unicode without normalizing or executing markup", () => {
  const field = definition.definition.fields.find((item) => item.name === "claimantName")!;
  const value = "A\u0308 <script> العربية\u200D\u202E";
  render(
    <>
      <BusinessValue value={value} field={field} context="ui.after" />
      <CharacterWarning value={null} />
    </>,
  );
  expect(screen.getByText(`After: ${value}`).tagName).toBe("BDI");
  expect(screen.getByText("Contains U+200D, U+202E")).toBeVisible();
  expect(document.querySelector("script")).toBeNull();
});

it("presents closed correction targets through groups while preserving exact action tokens", () => {
  const en = createPresenter({ language: "en", displayLocale: "en-GB" });
  expect(en.authoredTargetLabel("registration.action")).toBe("Registration · Action");
  expect(en.authoredTargetLabel("decision.action")).toBe("Payment decision · Action");
  expect(en.authoredTargetLabel("payment.action")).toBe("Payment record · Action");
  expect(en.authoredTargetLabel("claimedAmount")).toBe("Amount claimed");
  expect(en.token("REPLACE")).toBe("Replace details");
  for (const language of ["lv", "ar"] as const) {
    const p = createPresenter({ language, displayLocale: "en-GB" });
    for (const group of ["registration", "decision", "payment"]) {
      expect(p.authoredTargetLabel(`${group}.action`)).toContain(p.groupLabel(group));
      expect(p.authoredTargetLabel(`${group}.action`)).not.toContain(".action");
    }
    expect(p.correctionTargetGroup("claimedAmount")).toBeNull();
  }
});
