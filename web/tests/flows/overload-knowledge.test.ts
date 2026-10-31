import { prepareRequest } from "../../src/protocol/preparedRequest";
import { beforeEach, expect, it, vi } from "vitest";
import { isMutationUncertain, service } from "../../src/api/service";
import { operationId } from "../support/service-ui.fixtures";
const overloaded = (executionPhase: string | null) =>
  new Response(
    JSON.stringify({
      hostFailure: {
        code: "RATE_LIMITED",
        phase: executionPhase,
      },
    }),
    {
      status: 429,
      headers: {
        "content-type": "application/json",
      },
    },
  );
beforeEach(() => vi.stubGlobal("fetch", vi.fn()));
it("classifies verified overload as not started and refuses the old ambiguous busy body", async () => {
  const fetch = vi.mocked(globalThis.fetch);
  const draft = {
    kind: "CASE",
    operationId,
    caseReference: "SYNTHETIC",
    expectedRevision: 1,
    act: {
      kind: "CLOSE",
    },
  } as const;
  fetch.mockResolvedValueOnce(overloaded("NOT_STARTED"));
  const refused = await service.execute(
    prepareRequest("operation.execute", draft),
    "token",
    "90000000-0000-4000-8000-000000000001",
  );
  expect(refused).toMatchObject({
    kind: "hostFailure",
    status: 429,
  });
  expect(isMutationUncertain(refused)).toBe(false);
  fetch.mockResolvedValueOnce(overloaded(null));
  const invalid = await service.execute(
    prepareRequest("operation.execute", draft),
    "token",
    "90000000-0000-4000-8000-000000000001",
  );
  expect(invalid.kind).toBe("deliveryFailure");
  expect(isMutationUncertain(invalid)).toBe(true);
});
