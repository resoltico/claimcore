import { it, expect } from "vitest";
import { isHostFailure } from "../src/generated/contracts/validation";
import { hostCases } from "./contract-corpus.fixtures";
it("host failures bind actual HTTP status and forbid extra diagnostics", async () => {
  for (const c of hostCases()) {
    expect(await isHostFailure(c.value, c.status)).toBe(true);
    expect(await isHostFailure(c.value, 403)).toBe(false);
    expect(await isHostFailure({ ...c.value, unexpected: true }, c.status)).toBe(false);
  }
});
