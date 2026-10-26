import { expect, it } from "vitest";
import { createPresenter } from "../src/presentation/presenter";
import { defaults } from "../src/presentation/preferences";
import { definition } from "./service-ui.fixtures";

it("gives exact scalar syntax and limits for each editable scalar", () => {
  const p = createPresenter(defaults);
  const hint = (name: string) =>
    p.hint(definition.catalog.fields.find((field) => field.name === name)!);
  expect(hint("caseReference")).toBe(
    "Use 1 to 80 printable ASCII characters without spaces. Letter case identifies the same case; the original spelling is retained.",
  );
  expect(hint("claimantName")).toBe("Up to 200 characters.");
  expect(hint("incidentDate")).toBe(
    "Use ASCII digits in YYYY-MM-DD format, for example 2026-10-06.",
  );
  expect(hint("claimedAmount")).toBe(
    "Use ASCII digits and a dot, for example 0.10; up to 4 decimal places. No redundant leading zeros (00.10), signs or commas.",
  );
  expect(hint("claimedCurrency")).toBe("3 uppercase ASCII letters (A–Z).");
  expect(hint("status")).toBe("Open, Closed");
});
