import { expect, it, vi } from "vitest";
import { resultMessage, v2 } from "../src/api/v2";
import { operationId, preparation, response } from "./v2-ui.fixtures";

it("names idempotent recovery outcomes and leaves downloads without outcome messages", async () => {
  vi.stubGlobal("fetch", vi.fn());
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(response("recovery.dismiss", "ALREADY_DISMISSED", preparation));
  expect(resultMessage(await v2.recoveryDismiss(operationId, "a".repeat(64), "token"))).toBe(
    "Preparation was already dismissed.",
  );
  fetch.mockResolvedValueOnce(response("recovery.importRecordRetain", "EXISTING", preparation));
  const file = new File(["{}"], "recovery.json");
  expect(resultMessage(await v2.importRecordRetain(file, "a".repeat(64), "token"))).toBe(
    "Recovery material was already retained.",
  );
  expect(
    resultMessage({ kind: "outcome", status: 200, value: { blob: new Blob(), filename: "x" } }),
  ).toBe("The operation did not complete.");
});
