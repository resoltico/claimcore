import { afterEach, it, expect, vi } from "vitest";
import { isMutationUncertain } from "../../../src/api/service";
import type { ApiResult, EndpointOutcome } from "../../../src/api/service";
it("explicit uncertainty is retained while definite rejection and cancellation remain definite", () => {
  const unknown: ApiResult<EndpointOutcome> = {
    kind: "outcome",
    status: 200,
    value: {
      operationId: "11111111-1111-4111-8111-111111111111",
      outcome: { kind: "OUTCOME_UNKNOWN", data: null },
    },
  };
  expect(isMutationUncertain(unknown)).toBe(true);
  expect(
    isMutationUncertain({
      kind: "outcome",
      status: 200,
      value: {
        operationId: "11111111-1111-4111-8111-111111111111",
        outcome: { kind: "FAILED", data: { code: "CORE_STORE_UNAVAILABLE", action: "RETRY_SAFE" } },
      },
    }),
  ).toBe(false);
  expect(
    isMutationUncertain({
      kind: "deliveryFailure",
      notice: { kind: "local", reason: "unreachable" },
    }),
  ).toBe(true);
});

import { service } from "../../../src/api/service";
import { prepareRequest } from "../../../src/protocol/preparedRequest";
const operation = "10000000-0000-4000-8000-000000000001";
const installation = "90000000-0000-4000-8000-000000000001";
const receipt = {
  operationId: operation,
  position: { sequence: 1, entryHash: "a".repeat(64) },
  recordedAt: "2026-01-01T00:00:00.000000Z",
  recordedBy: operation,
  subject: {
    kind: "CASE",
    caseId: operation,
    caseReference: "CORRELATION",
    revision: 1,
    act: "OPEN",
    snapshot: { kind: "WITHHELD" },
  },
};
const prepared = () =>
  prepareRequest("operation.execute", {
    kind: "CASE",
    operationId: operation,
    target: { kind: "REFERENCE", caseReference: "CORRELATION" },
    expectedRevision: 0,
    act: { kind: "CLOSE" },
  });
const reply = (value: unknown) =>
  new Response(JSON.stringify(value), {
    status: 200,
    headers: { "content-type": "application/json" },
  });
afterEach(() => vi.unstubAllGlobals());
it("real fetch admission checks execute envelope and receipt identities before accepting", async () => {
  vi.stubGlobal("fetch", vi.fn());
  const matching = { operationId: operation, outcome: { kind: "ACCEPTED", data: receipt } };
  vi.mocked(fetch).mockResolvedValueOnce(reply(matching));
  expect((await service.execute(prepared(), "csrf", installation)).kind).toBe("outcome");
  const other = "90000000-0000-4000-8000-000000000099";
  for (const mismatched of [
    { ...matching, operationId: other },
    { ...matching, outcome: { kind: "ACCEPTED", data: { ...receipt, operationId: other } } },
  ]) {
    vi.mocked(fetch).mockResolvedValueOnce(reply(mismatched));
    const result = await service.execute(prepared(), "csrf", installation);
    expect(result.kind).toBe("deliveryFailure");
    expect(isMutationUncertain(result)).toBe(true);
  }
});
it("resolve and observe reject a different operation envelope and matching final replies pass", async () => {
  vi.stubGlobal("fetch", vi.fn());
  for (const endpoint of ["resolve", "observe"] as const) {
    const outcome =
      endpoint === "resolve"
        ? { kind: "NOT_ACCEPTED", data: null }
        : { kind: "SUCCEEDED", data: receipt };
    vi.mocked(fetch).mockResolvedValueOnce(reply({ operationId: operation, outcome }));
    const matching =
      endpoint === "resolve"
        ? await service.resolve(operation, "csrf", installation)
        : await service.observe(operation, "csrf");
    expect(matching.kind).toBe("outcome");
    vi.mocked(fetch).mockResolvedValueOnce(
      reply({ operationId: "90000000-0000-4000-8000-000000000099", outcome }),
    );
    const refused =
      endpoint === "resolve"
        ? await service.resolve(operation, "csrf", installation)
        : await service.observe(operation, "csrf");
    expect(refused.kind).toBe("deliveryFailure");
  }
});
