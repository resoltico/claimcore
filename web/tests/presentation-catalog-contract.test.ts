import { expect, it } from "vitest";
import { definition } from "./service-ui.fixtures";
import { renderKey } from "../src/presentation/messages";
import { defaults } from "../src/presentation/preferences";
import { createPresenter } from "../src/presentation/presenter";
import requirements from "../src/presentation/generated/diagnostic-parameters.json";
const { descriptors } = definition;
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
  for (const field of descriptors.fields) {
    expect(p.fieldLabel(field.name)).toBe(renderKey(defaults, field.labelId));
    expect(p.fieldMeaning(field.name)).toBe(renderKey(defaults, field.meaningId));
  }
  for (const command of descriptors.commands) {
    expect(p.commandLabel(command.kind)).toBe(renderKey(defaults, command.labelId));
    expect(p.commandMeaning(command.kind)).toBe(renderKey(defaults, command.meaningId));
    if (command.inputs.kind === "CORRECTION_GROUPS") {
      for (const group of command.inputs.groups) {
        expect(p.groupLabel(group.name)).toBe(renderKey(defaults, group.labelId));
        expect(p.groupMeaning(group.name)).toBe(renderKey(defaults, group.meaningId));
      }
    }
  }
});
