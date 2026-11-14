import { prepareRequest } from "../../src/protocol/preparedRequest";
import { it, expect, vi, afterEach } from "vitest";
import { service, isMutationUncertain } from "../../src/api/service";
afterEach(() => vi.unstubAllGlobals());
it("fetch never follows redirects and dispatched delivery loss remains uncertain", async () => {
  vi.stubGlobal(
    "fetch",
    vi.fn<typeof globalThis.fetch>().mockRejectedValue(new Error("Synthetic transport")),
  );
  const result = await service.execute(
    prepareRequest("operation.execute", {
      operationId: "10000000-0000-4000-8000-000000000001",
      task: "case.close",
      binding: {
        kind: "CASE",
        target: { kind: "REFERENCE", caseReference: "CASE-1" },
        expectedRevision: 1,
      },
      inputs: {},
    } as const),
    "csrf",
    "90000000-0000-4000-8000-000000000001",
  );
  expect(isMutationUncertain(result)).toBe(true);
  expect(vi.mocked(fetch).mock.calls[0]?.[1]).toMatchObject({
    redirect: "error",
    credentials: "same-origin",
    cache: "no-store",
  });
});
