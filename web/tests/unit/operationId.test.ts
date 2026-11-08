import { afterEach, describe, expect, it, vi } from "vitest";
import { operationId, operationIssuedAt } from "../../src/operation/operationId";

afterEach(() => vi.restoreAllMocks());

describe("operation UUIDv7", () => {
  it("encodes an independently known Unix millisecond timestamp and RFC bits", () => {
    vi.spyOn(Date, "now").mockReturnValue(1_700_000_000_000);
    vi.spyOn(crypto, "getRandomValues").mockImplementation((bytes) => {
      (bytes as Uint8Array).fill(0);
      return bytes;
    });
    expect(operationId()).toBe("018bcfe5-6800-7000-8000-000000000000");
    expect(operationIssuedAt("018bcfe5-6800-7fff-bfff-ffffffffffff")).toBe(1_700_000_000_000);
  });

  it("retains random payload bits while fixing version and variant", () => {
    vi.spyOn(Date, "now").mockReturnValue(1_700_000_000_001);
    vi.spyOn(crypto, "getRandomValues").mockImplementation((bytes) => {
      (bytes as Uint8Array).fill(255);
      return bytes;
    });
    expect(operationId()).toBe("018bcfe5-6801-7fff-bfff-ffffffffffff");
  });
});
