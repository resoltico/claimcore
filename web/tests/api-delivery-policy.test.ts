import { prepareRequest } from "../src/protocol/preparedRequest";
import { it, expect, vi, afterEach } from "vitest";
import { service, isMutationUncertain } from "../src/api/service";
afterEach(() => vi.unstubAllGlobals());
it("fetch never follows redirects and dispatched delivery loss remains uncertain", async () => {
  vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new Error("Synthetic transport")));
  const result = await service.execute(
    prepareRequest("operation.execute", {
      kind: "CASE",
      operationId: "10000000-0000-4000-8000-000000000001",
      caseReference: "CASE-1",
      expectedRevision: 1,
      act: {
        kind: "CLOSE",
      },
    } as const),
    "csrf",
  );
  expect(isMutationUncertain(result)).toBe(true);
  expect(vi.mocked(fetch).mock.calls[0]?.[1]).toMatchObject({
    redirect: "error",
    credentials: "same-origin",
    cache: "no-store",
  });
});
