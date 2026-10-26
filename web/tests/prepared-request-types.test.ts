import { expect, it } from "vitest";
import type { service } from "../src/api/service";
import { strictByteProfile } from "@contracts/client";
import { prepareRequest } from "../src/protocol/preparedRequest";
it("the compiler rejects raw bytes and structural forgeries at mutation submission", () => {
  const boundary = (_prepared: Parameters<typeof service.execute>[0]): boolean => true;
  // @ts-expect-error -- This rejecting control must fail compilation if raw strings become admissible.
  expect(boundary("{}")).toBe(true);
  const source = {
    kind: "CASE",
    operationId: "10000000-0000-4000-8000-000000000001",
    caseReference: "CASE-1",
    expectedRevision: 1,
    act: {
      kind: "CLOSE",
    },
  } as const;
  const forged = { endpoint: "operation.execute", bytes: "{}", input: source } as const;
  // @ts-expect-error -- A valid structural input still lacks the private prepared identity.
  expect(boundary(forged)).toBe(true);
  const prepared = prepareRequest("operation.execute", source);
  expect(boundary(prepared)).toBe(true);
  expect(Object.isFrozen(prepared.input.act)).toBe(true);
  expect(prepared.bytes).toBe(JSON.stringify(source));
});
it("final prepared wire admission bounds actual UTF-8 bytes before dispatch", () => {
  const source = {
    kind: "CASE",
    operationId: "10000000-0000-4000-8000-000000000001",
    caseReference: "CASE-1",
    expectedRevision: 1,
    act: {
      kind: "VOID",
      reason: "💼".repeat(strictByteProfile.maximumRequestBytes),
    },
  } as const;
  expect(() => prepareRequest("operation.execute", source)).toThrow("wire format");
});
