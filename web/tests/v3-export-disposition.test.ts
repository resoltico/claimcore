import { recoveryArtifact } from "./recovery-artifact.fixtures";
import { beforeEach, expect, it, vi } from "vitest";
import { v3 } from "../src/api/v3";
import { operationId } from "./v3-ui.fixtures";

const filename = `claimcore-recovery-${operationId}.json`;
const download = (disposition: string | null): Response =>
  new Response(JSON.stringify(recoveryArtifact), {
    headers: {
      "content-type": "application/vnd.claimcore.recovery+json",
      ...(disposition === null ? {} : { "content-disposition": disposition }),
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
    [`attachment; filename=${filename}`, false],
    [`attachment; filename=${filename}; flag`, false],
    [`attachment; filename=${filename}; name=${filename}`, false],
    [null, false],
  ] as const) {
    fetch.mockResolvedValueOnce(download(header));
    const result = await v3.recoveryExport(operationId, "a".repeat(64), "token");
    expect(result.kind === "outcome", String(header)).toBe(accepted);
  }
});
