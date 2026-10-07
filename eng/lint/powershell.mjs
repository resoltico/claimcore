// Windows' native parser owns PowerShell syntax and executable-unit bounds.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { assertNoLinkAbove } from "../ci/scan/files.mjs";
import { executable } from "../ci/executable.mjs";

/** @param {string[]} files */
export function checkPowerShell(files) {
  assert.equal(process.platform, "win32", "Native PowerShell policy requires Windows.");
  const checker = fileURLToPath(new URL("./check-powershell.ps1", import.meta.url));
  const limits = fileURLToPath(new URL("../../config/oxlint.json", import.meta.url));
  const paths = [checker, ...files];
  paths.forEach(assertNoLinkAbove);
  assertNoLinkAbove(limits);
  const result = spawnSync(
    executable("powershell"),
    ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", readFileSync(checker, "utf8")],
    {
      env: {
        ...process.env,
        CLAIMCORE_PS_FILES: JSON.stringify(paths),
        CLAIMCORE_PS_LIMITS: limits,
      },
      stdio: "pipe",
      timeout: 30_000,
    },
  );
  assert.equal(result.status, 0, "Native PowerShell source policy was refused.");
}
