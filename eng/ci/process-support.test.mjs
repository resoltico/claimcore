import assert from "node:assert/strict";
import { mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { runToLog } from "./process-support.mjs";

test("startup failure settles and closes its log once even when close follows error", async () => {
  const root = mkdtempSync(join(tmpdir(), "claimcore-process-test-"));
  try {
    assert.equal(
      await runToLog(join(root, "absent-executable"), [], {
        cwd: root,
        log: join(root, "process.log"),
      }),
      127,
    );
    // The close event is asynchronous after the error event.
    await new Promise((resolve) => {
      setImmediate(resolve);
    });
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
