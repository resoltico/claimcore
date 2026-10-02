import { beforeEach, expect, it, vi } from "vitest";
import { isMutationUncertain, v3 } from "../src/api/v3";
import { createDraft } from "../src/domain/metadata";
import { operationId } from "./v3-ui.fixtures";

const overloaded = (executionPhase: string | null) =>
  new Response(
    JSON.stringify({
      kind: "HOST_FAILURE",
      code: "WEB_BUSY",
      status: 429,
      diagnostic: { id: "WEB_HOST_BUSY", parameters: {} },
      message: "Synthetic admission overload",
      executionPhase,
    }),
    { status: 429, headers: { "content-type": "application/json" } },
  );

beforeEach(() => vi.stubGlobal("fetch", vi.fn()));

it("classifies verified overload as not started and refuses the old ambiguous busy body", async () => {
  const fetch = vi.mocked(globalThis.fetch);
  const draft = createDraft(operationId, "SYNTHETIC", "1", "CLOSE", {});
  fetch.mockResolvedValueOnce(overloaded("NOT_STARTED"));
  const refused = await v3.submit(draft, "token");
  expect(refused).toMatchObject({ kind: "hostFailure", status: 429 });
  expect(isMutationUncertain(refused)).toBe(false);
  fetch.mockResolvedValueOnce(overloaded(null));
  const invalid = await v3.submit(draft, "token");
  expect(invalid.kind).toBe("deliveryFailure");
  expect(isMutationUncertain(invalid)).toBe(true);
});
