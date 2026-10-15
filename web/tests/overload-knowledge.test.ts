import { beforeEach, expect, it, vi } from "vitest";
import { isMutationUncertain, service } from "../src/api/service";
import { createDraft } from "../src/domain/metadata";
import { operationId } from "./service-ui.fixtures";

const overloaded = (executionPhase: string | null) =>
  new Response(
    JSON.stringify({
      kind: "HOST_FAILURE",
      code: "RATE_LIMITED",
      status: 429,
      diagnostic: { id: "HOST_RATELIMITED", parameters: {} },
      message: "Request admission is busy.",
      executionPhase,
    }),
    { status: 429, headers: { "content-type": "application/json" } },
  );

beforeEach(() => vi.stubGlobal("fetch", vi.fn()));

it("classifies verified overload as not started and refuses the old ambiguous busy body", async () => {
  const fetch = vi.mocked(globalThis.fetch);
  const draft = createDraft(operationId, "SYNTHETIC", 1, "CLOSE", {});
  fetch.mockResolvedValueOnce(overloaded("NOT_STARTED"));
  const refused = await service.execute(JSON.stringify(draft), "token");
  expect(refused).toMatchObject({ kind: "hostFailure", status: 429 });
  expect(isMutationUncertain(refused)).toBe(false);
  fetch.mockResolvedValueOnce(overloaded(null));
  const invalid = await service.execute(JSON.stringify(draft), "token");
  expect(invalid.kind).toBe("deliveryFailure");
  expect(isMutationUncertain(invalid)).toBe(true);
});
