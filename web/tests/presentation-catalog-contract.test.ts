import { expect, it } from "vitest";
import { definition } from "./service-ui.fixtures";
import { renderKey } from "../src/presentation/messages";
import { defaults } from "../src/presentation/preferences";
import { createPresenter } from "../src/presentation/presenter";
import { diagnosticParameters as requirements } from "@contracts/messages";
const { catalog } = definition;
it("renders every admitted diagnostic identity in every real language", () => {
  const limits: Record<
    string,
    Record<string, { minimum?: number; enum?: ReadonlyArray<string> }>
  > = requirements;
  for (const [id, parameters] of Object.entries(limits)) {
    const values = Object.fromEntries(
      Object.entries(parameters).map(([key, rule]) => [
        key,
        rule.enum?.[0] ?? String(rule.minimum ?? 0),
      ]),
    );
    for (const language of ["en", "lv", "ar"] as const) {
      const value = renderKey({ language, displayLocale: "en-GB" }, id, values);
      expect(value, id).not.toBe(id);
      expect(value.length, id).toBeGreaterThan(0);
      expect(value, id).not.toMatch(/\{[a-zA-Z]+\}/u);
    }
  }
});
it("preserves independent English diagnostics and parameter formatting", () => {
  expect(renderKey(defaults, "diagnostic.INPUT_TEXT_TOO_LONG", { maximumCharacters: "200" })).toBe(
    "The value must not exceed 200 Unicode characters.",
  );
  expect(renderKey(defaults, "host.RESPONSE_FAILED")).toBe(
    "The completed response could not be delivered.",
  );
});
it("resolves business meaning through descriptor message identities", () => {
  const p = createPresenter(defaults);
  for (const field of catalog.fields) {
    expect(p.fieldLabel(field.name)).toBe(renderKey(defaults, field.labelId));
    expect(p.fieldMeaning(field.name)).toBe(renderKey(defaults, field.meaningId));
  }
  for (const entry of catalog.entries.filter(
    (item) => item.scope === "CASE" && item.endpoint === "operation.execute",
  )) {
    expect(p.actLabel(entry.kind)).toBe(renderKey(defaults, entry.labelId));
    expect(p.actMeaning(entry.kind)).toBe(renderKey(defaults, entry.meaningId));
    for (const input of entry.inputs.filter(
      (item) => item.kind.kind === "GROUP" && item.diagnosticTargets.length > 0,
    )) {
      expect(p.groupLabel(input.diagnosticTargets[0]!)).toBe(renderKey(defaults, input.labelId));
      expect(p.groupMeaning(input.diagnosticTargets[0]!)).toBe(
        renderKey(defaults, input.meaningId!),
      );
    }
  }
});
