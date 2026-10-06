import { expect, it } from "vitest";
import { createPresenter } from "../src/presentation/presenter";
import { defaults } from "../src/presentation/preferences";
import { definition } from "./v3-ui.fixtures";

it("gives exact scalar syntax and limits from each descriptor rather than an absent hint", () => {
  const p = createPresenter(defaults);
  const hint = (name: string) =>
    p.hint(definition.definition.fields.find((field) => field.name === name)!);
  expect(hint("caseReference")).toBe("Up to 80 characters.");
  expect(hint("claimantName")).toBe("Up to 200 characters.");
  expect(hint("incidentDate")).toBe(
    "Use ASCII digits in YYYY-MM-DD format, for example 2026-10-06.",
  );
  expect(hint("claimedAmount")).toBe(
    "Use ASCII digits and a dot, for example 125.50; up to 4 decimal places. No signs or commas.",
  );
  expect(hint("claimedCurrency")).toBe("3 uppercase ASCII letters (A–Z).");
  expect(hint("status")).toBe("Open, Closed");
});
