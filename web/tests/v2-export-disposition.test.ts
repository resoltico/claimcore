import { beforeEach, expect, it, vi } from "vitest";
import { v2 } from "../src/api/v2";
import { operationId } from "./v2-ui.fixtures";

const filename = `claimcore-recovery-${operationId}.json`;
const download = (disposition: string): Response =>
  new Response("{}", {
    headers: {
      "content-type": "application/vnd.claimcore.recovery+json",
      "content-disposition": disposition,
    },
  });

beforeEach(() => vi.stubGlobal("fetch", vi.fn()));

it("requires one exact attachment filename and matching UTF-8 filename parameter", async () => {
  const fetch = vi.mocked(globalThis.fetch);
  for (const [header, accepted] of [
    [`attachment; filename=${filename}; filename*=UTF-8''${filename}`, true],
    [`attachment; filename="${filename}"; filename*=UTF-8''${filename}`, true],
    [`attachment; filename=${filename}.evil; filename*=UTF-8''${filename}`, false],
    [`attachment; filename=${filename}; filename=${filename}`, false],
    [`attachment; filename=${filename}; filename*=UTF-8''${filename}.evil`, false],
    [`inline; filename=${filename}; filename*=UTF-8''${filename}`, false],
  ] as const) {
    fetch.mockResolvedValueOnce(download(header));
    const result = await v2.recoveryExport(operationId, "a".repeat(64), "token");
    expect(result.kind === "outcome", header.includes(".evil") ? "suffix" : "attachment").toBe(
      accepted,
    );
  }
});
