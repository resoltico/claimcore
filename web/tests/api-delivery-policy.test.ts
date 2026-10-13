import { afterEach, expect, it, vi } from "vitest";
import { isMutationUncertain, v3 } from "../src/api/v3";
import { createDraft } from "../src/domain/metadata";
import { operationId } from "./v3-ui.fixtures";

afterEach(() => vi.unstubAllGlobals());

it("ordinary API and recovery export refuse redirects with one delivery policy and no replay", async () => {
  const fetch = vi
    .fn<typeof globalThis.fetch>()
    .mockRejectedValue(new TypeError("synthetic refused redirect"));
  vi.stubGlobal("fetch", fetch);
  const draft = createDraft(operationId, "SYNTHETIC", "1", "CLOSE", {});
  const original = JSON.stringify(draft);
  const result = await v3.submit(draft, "synthetic-token");
  expect(isMutationUncertain(result)).toBe(true);
  expect(await v3.recoveryExport(operationId, "a".repeat(64), "synthetic-token")).toMatchObject({
    kind: "deliveryFailure",
  });
  expect(fetch).toHaveBeenCalledTimes(2);
  expect(fetch.mock.calls[0]?.[1]?.body).toBe(original);
  for (const [, options] of fetch.mock.calls) {
    expect(options).toMatchObject({ redirect: "error", credentials: "same-origin" });
    expect(options?.signal).toBeInstanceOf(AbortSignal);
  }
  expect(JSON.stringify(draft)).toBe(original);
});
