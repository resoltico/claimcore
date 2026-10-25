import { expect, it } from "vitest";
import { buildRequest, isHostFailure, isRequest, requestIssue } from "@contracts/client";

it("generated host checks enforce status, code, phase and closed shape together", () => {
  const accepted = { hostFailure: { code: "INPUT_INVALID", phase: "NOT_STARTED" } };
  expect(isHostFailure(accepted, 400)).toBe(true);
  expect(isHostFailure(accepted, 413)).toBe(false);
  for (const value of [
    { hostFailure: { code: "BODY_TOO_LARGE", phase: "NOT_STARTED" } },
    { hostFailure: { code: "INPUT_INVALID", phase: "STARTED_UNCONFIRMED" } },
    { hostFailure: { code: "INPUT_INVALID", phase: null } },
    { hostFailure: { code: "INPUT_INVALID", phase: "NOT_STARTED", message: "foreign" } },
    { hostFailure: { code: "INPUT_INVALID", phase: "NOT_STARTED" }, extra: true },
    {},
  ]) {
    expect(isHostFailure(value, 400)).toBe(false);
  }
});

it("generated draft assembly and complete admission remain distinct", () => {
  expect(buildRequest("operation.observe", { operationId: "" })).toEqual({ operationId: "" });
  expect(isRequest("operation.observe", { operationId: "" })).toBe(false);
  expect(
    requestIssue("operation.retry", { recoveryId: "invalid", operationId: "invalid" }),
  ).toEqual(["recoveryId"]);
  expect(() => buildRequest("operation.observe", { operationId: 7 })).toThrow();
  expect(isRequest("operation.pending", { limit: 51 })).toBe(false);
  expect(isRequest("operation.pending", { limit: 50 })).toBe(true);
});
