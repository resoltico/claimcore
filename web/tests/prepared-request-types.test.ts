import { expect, it } from "vitest";
import type { service } from "../src/api/service";
import { prepareRequest } from "../src/protocol/preparedRequest";
import { createDraft } from "../src/interaction/metadata";
it("the compiler rejects raw bytes and structural forgeries at mutation submission", () => {
  const boundary = (_prepared: Parameters<typeof service.execute>[0]): boolean => true;
  // @ts-expect-error -- This rejecting control must fail compilation if raw strings become admissible.
  expect(boundary("{}")).toBe(true);
  // @ts-expect-error -- Structural forgery lacks the protocol boundary's private prepared identity.
  expect(boundary({ endpoint: "case.execute", bytes: "{}", input: {} })).toBe(true);
  const source = createDraft("10000000-0000-4000-8000-000000000001", "CASE-1", 1, "CLOSE", {});
  const prepared = prepareRequest("case.execute", source);
  expect(boundary(prepared)).toBe(true);
  expect(Object.isFrozen(prepared.input.act)).toBe(true);
  expect(prepared.bytes).toBe(JSON.stringify(source));
});
