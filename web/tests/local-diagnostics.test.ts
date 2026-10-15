import { it, expect } from "vitest";
import { isHostFailure } from "../src/api/responseGuards";
import { hostCases } from "./contract-corpus.fixtures";
it("host failures bind actual HTTP status and forbid extra diagnostic parameters", () => {
  for (const c of hostCases()) {
    expect(isHostFailure(c.value, c.status)).toBe(true);
    expect(isHostFailure(c.value, 403)).toBe(false);
    expect(
      isHostFailure(
        { ...c.value, diagnostic: { ...c.value.diagnostic, parameters: { unexpected: 1 } } },
        c.status,
      ),
    ).toBe(false);
  }
});
