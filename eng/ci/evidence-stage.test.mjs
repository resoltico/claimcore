import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdtempSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { evidenceStageOwnership } from "./evidence-stage.mjs";

test("noncanonical staging roots refuse before ancestor enumeration", () => {
  const root = mkdtempSync(join(realpathSync(tmpdir()), "claimcore-stage-path-"));
  try {
    writeFileSync(join(root, "report.trx"), "synthetic report");
    assert.notEqual(evidenceStageOwnership(root, ["report.trx"]), null);
    const module = new URL("./evidence-stage.mjs", import.meta.url).href;
    const script = `import assert from "node:assert/strict";
      import { evidenceStageOwnership } from ${JSON.stringify(module)};
      assert.equal(evidenceStageOwnership(process.argv[1], ["report.trx"]), null);`;
    for (const path of [".", `${root}/`]) {
      execFileSync(process.execPath, ["--input-type=module", "-e", script, path], {
        cwd: root,
        timeout: 5000,
        stdio: "pipe",
      });
    }
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
