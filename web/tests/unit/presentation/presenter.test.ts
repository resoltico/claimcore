import { expect, it } from "vitest";
import { createPresenter } from "../../../src/presentation/presenter";
import { defaults } from "../../../src/presentation/preferences";
import { definition } from "../../support/service-ui.fixtures";

it("keeps human widget hints separate from precise catalog rules and formats declared rule limits", () => {
  const p = createPresenter(defaults);
  const hint = (name: string) =>
    p.hint(definition.catalog.fields.find((field) => field.name === name)!);
  expect(hint("caseReference")).toBe(
    "Your own reference for this case, for example CLM-2026-0001. It cannot be changed later.",
  );
  expect(hint("claimantName")).toBe("The person or company making the claim.");
  expect(hint("incidentDate")).toBe("When the incident happened.");
  expect(hint("claimedAmount")).toBe("How much the claimant is asking for.");
  expect(hint("claimedCurrency")).toBe("The currency of the claimed amount.");
  expect(p.fieldRule("caseReference")).toContain("80 printable ASCII characters");
  expect(p.fieldRule("claimantName")).toBe("Up to 200 characters.");
  expect(p.fieldRule("claimedAmount")).toContain("4 after it");
  expect(hint("caseReference")).not.toContain("ASCII");
});

it("keeps input-specific bounds when rules share one wording key", () => {
  const p = createPresenter(defaults);
  expect(p.fieldRule("claimantName")).toBe("Up to 200 characters.");
  expect(p.fieldRule("text")).toBe("Up to 100 characters.");
});
