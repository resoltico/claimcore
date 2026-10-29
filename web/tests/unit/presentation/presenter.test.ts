import { expect, it } from "vitest";
import { createPresenter } from "../../../src/presentation/presenter";
import { defaults } from "../../../src/presentation/preferences";
import { definition } from "../../support/service-ui.fixtures";

it("gives interface entry guidance and exact limits for each editable scalar", () => {
  const p = createPresenter(defaults);
  const hint = (name: string) =>
    p.hint(definition.catalog.fields.find((field) => field.name === name)!);
  expect(hint("caseReference")).toBe(
    "Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained.",
  );
  expect(hint("claimantName")).toBe("Up to 200 characters.");
  expect(hint("incidentDate")).toBe(
    "Enter the date in these sections or choose it from the calendar.",
  );
  expect(hint("claimedAmount")).toBe(
    "You can use the selected number format. Use up to 4 decimal places, without signs or redundant leading zeros.",
  );
  expect(hint("claimedCurrency")).toBe("3 uppercase ASCII letters (A–Z).");
  expect(hint("status")).toBe("Open, Closed");
});
