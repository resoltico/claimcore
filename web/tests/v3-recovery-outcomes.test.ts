import { expect, it, vi } from "vitest";
import { resultNotice, v3 } from "../src/api/v3";
import { operationId, preparation, response } from "./v3-ui.fixtures";

it("names idempotent recovery outcomes and leaves downloads without outcome messages", async () => {
  vi.stubGlobal("fetch", vi.fn());
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(response("recovery.dismiss", "ALREADY_DISMISSED", preparation));
  expect(resultNotice(await v3.recoveryDismiss(operationId, "a".repeat(64), "token"))).toEqual({
    kind: "local",
    reason: "alreadyDismissed",
  });
  fetch.mockResolvedValueOnce(response("recovery.importEnvelopeRetain", "EXISTING", preparation));
  const file = new File(["{}"], "recovery.json");
  expect(resultNotice(await v3.importEnvelopeRetain(file, "a".repeat(64), "token"))).toEqual({
    kind: "local",
    reason: "existing",
  });
  expect(
    resultNotice({ kind: "outcome", status: 200, value: { blob: new Blob(), filename: "x" } }),
  ).toEqual({ kind: "local", reason: "incomplete" });
});
