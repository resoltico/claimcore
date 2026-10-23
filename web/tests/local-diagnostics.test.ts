import { it, expect } from "vitest";
import { isHostFailure } from "../src/protocol/client";
import { hostCases } from "./contract-corpus.fixtures";
it("host failures bind actual HTTP status and forbid extra members", () => {
  for (const c of hostCases()) {
    expect(isHostFailure(c.value, c.status)).toBe(true);
    expect(isHostFailure(c.value, 403)).toBe(false);
    expect(
      isHostFailure({ hostFailure: { ...c.value.hostFailure, unexpected: 1 } }, c.status),
    ).toBe(false);
  }
});
