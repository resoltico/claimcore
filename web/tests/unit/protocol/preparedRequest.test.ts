import { expect, it } from "vitest";
import { prepareRequest } from "../../../src/protocol/preparedRequest";
import { valuesFor, requestFor } from "../../support/operation-form.fixtures";
import { type service } from "../../../src/api/service";
import { strictByteProfile } from "@contracts/client";

it("detaches replacement values and preserves decimal spelling after authoring changes", () => {
  const values = {
    ...valuesFor("CORRECT"),
    "act.groups.registration.kind": "UPDATE",
    "act.groups.registration.values.claimantName": "Before",
    "act.groups.registration.values.claimed.amount": "1.0000",
    "act.groups.registration.values.claimed.currency": "EUR",
  };
  const request = requestFor(values, "CORRECT");
  const prepared = prepareRequest("operation.execute", request);
  Object.assign(values, { "act.groups.registration.values.claimantName": "After" });
  if (
    request.kind !== "CASE" ||
    request.act.kind !== "CORRECT" ||
    request.act.groups.registration.kind !== "UPDATE"
  ) {
    throw new Error("Missing correction fixture.");
  }
  Object.assign(request.act.groups.registration.values, { claimantName: "Changed" });
  expect(prepared.bytes).toContain('"claimantName":"Before"');
  expect(prepared.bytes).toContain('"amount":"1.0000"');
  expect(prepared.input).not.toBe(request);
});

it("the compiler rejects raw bytes and structural forgeries at mutation submission", () => {
  const boundary = (_prepared: Parameters<typeof service.execute>[0]): boolean => true;
  // @ts-expect-error -- This rejecting control must fail compilation if raw strings become admissible.
  expect(boundary("{}")).toBe(true);
  const source = {
    kind: "CASE",
    operationId: "10000000-0000-4000-8000-000000000001",
    target: { kind: "REFERENCE", caseReference: "CASE-1" },
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
    target: { kind: "REFERENCE", caseReference: "CASE-1" },
    expectedRevision: 1,
    act: {
      kind: "VOID",
      reason: "💼".repeat(strictByteProfile.maximumRequestBytes),
    },
  } as const;
  expect(() => prepareRequest("operation.execute", source)).toThrow("wire format");
});
