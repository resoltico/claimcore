import { describe, it, expect } from "vitest";
import { acceptedReceipt, isLocked } from "../src/interaction/editorSupport";
import { generatedWebValue } from "./contract-corpus.fixtures";
describe("operation outcomes", () => {
  it("locks only preview submission and uncertainty", () => {
    for (const state of ["PREVIEWING", "SUBMITTING", "OUTCOME_UNKNOWN"] as const) {
      expect(isLocked(state)).toBe(true);
    }
    for (const state of ["EDITING", "REVIEWING", "ACCEPTED", "DEFINITELY_REJECTED"] as const) {
      expect(isLocked(state)).toBe(false);
    }
  });
  it("accepts success and replay receipts only", () => {
    const result = generatedWebValue("case.execute");
    expect(acceptedReceipt(result)).not.toBeNull();
    expect(
      acceptedReceipt({
        endpoint: "case.execute",
        operationId: "11111111-1111-4111-8111-111111111111",
        outcome: { tag: "CANCELLED", data: null },
      }),
    ).toBeNull();
  });
});
