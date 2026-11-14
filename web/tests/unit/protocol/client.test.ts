import { it, expect } from "vitest";
import { isHostFailure } from "../../../src/protocol/client";
import { hostCases } from "../../support/host-failure.fixtures";

it("host failures bind actual HTTP status and forbid extra members", () => {
  for (const sample of hostCases()) {
    expect(isHostFailure(sample.value, sample.status)).toBe(true);
    expect(isHostFailure(sample.value, 403)).toBe(false);
    expect(
      isHostFailure({ hostFailure: { ...sample.value.hostFailure, unexpected: 1 } }, sample.status),
    ).toBe(false);
  }
});
