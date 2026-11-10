import { taskDefinitions, taskInputDefinitions } from "@contracts/client";
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
  for (const task of taskDefinitions.filter(
    (item) => item.kind === "OPERATION" && item.subject === "CASE",
  )) {
    expect(p.actLabel(task.act)).toBe(renderCatalogKey(defaults, task.labelId));
    expect(p.actMeaning(task.act)).toBe(renderCatalogKey(defaults, task.meaningId));
  }
  for (const group of taskInputDefinitions.filter((input) => input.kind === "GROUP")) {
    expect(renderCatalogKey(defaults, group.labelId)).not.toBe(
      renderCatalogKey(defaults, "notice.unknownDiagnostic"),
    );
    if (group.hintId !== null) expect(renderCatalogKey(defaults, group.hintId)).not.toBe("");
  }
});
