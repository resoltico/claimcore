import { expect, it } from "vitest";
import { definition } from "../support/service-ui.fixtures";
import { renderCatalogKey } from "../../src/presentation/messages";
import { defaults } from "../../src/presentation/preferences";
import { createPresenter } from "../../src/presentation/presenter";
const { catalog } = definition;
it("preserves independent English diagnostics and parameter formatting", () => {
  expect(renderCatalogKey(defaults, "diagnostic.TOO_LONG", { maximumCharacters: "200" })).toBe(
    "Use at most 200 characters.",
  );
  expect(renderCatalogKey(defaults, "host.RESPONSE_FAILED")).toBe(
    "The result could not be delivered.",
  );
});
it("resolves business meaning through catalog message identities", () => {
  const p = createPresenter(defaults);
  for (const field of catalog.fields) {
    expect(p.fieldLabel(field.name)).toBe(renderCatalogKey(defaults, field.labelId));
    expect(p.fieldHint(field.name)).toBe(renderCatalogKey(defaults, field.hintId));
  }
  for (const entry of catalog.entries.filter(
    (item) => item.subject === "CASE" && item.endpoint === "operation.execute",
  )) {
    expect(p.actLabel(entry.kind)).toBe(renderCatalogKey(defaults, entry.labelId));
    expect(p.actMeaning(entry.kind)).toBe(renderCatalogKey(defaults, entry.meaningId));
    for (const input of entry.inputs.filter(
      (item) => item.kind.kind === "GROUP" && item.diagnosticTargets.length > 0,
    )) {
      expect(p.groupLabel(input.diagnosticTargets[0]!)).toBe(
        renderCatalogKey(defaults, input.labelId),
      );
      expect(p.groupHint(input.diagnosticTargets[0]!)).toBe(
        renderCatalogKey(defaults, input.hintId!),
      );
    }
  }
});
