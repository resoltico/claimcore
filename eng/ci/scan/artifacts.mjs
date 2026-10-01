// Scan explicit artifact paths for credentials before they are uploaded. Nothing the scanner
// reports is shown: even redacted findings can carry data-bearing lines and file names. The only
// output distinguishes a scan that completed and refused its targets from one that could not run.
import { chmodSync, existsSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import { assertNoLinkAbove, fingerprint, regularFiles } from "./files.mjs";
import { runChild, scannerEnvironment } from "./process.mjs";

const overrideNames = [".gitleaks.toml", ".gitleaksignore"];

/**
 * @param {string} path
 * @returns {string} The absolute path of a link-free, non-empty tree of regular files.
 */
export function checkTree(path) {
  if (path.trim() === "" || path.includes("\0") || /[*?]/u.test(path)) {
    throw new Error("Invalid artifact path.");
  }
  const full = resolve(path);
  if (!existsSync(full)) {
    throw new Error("Missing artifact path.");
  }
  assertNoLinkAbove(full);
  if (regularFiles(full, { forbiddenNames: overrideNames }).length === 0) {
    throw new Error("Artifact tree is empty.");
  }
  return full;
}

/**
 * Run the scanner over each target; every target is scanned even after a finding.
 * @param {string} binary
 * @param {string[]} targets
 * @param {string} scratch A private directory holding the empty ignore file.
 * @returns {Promise<number>}
 */
async function scanTargets(binary, targets, scratch) {
  const emptyIgnore = join(scratch, "empty.gitleaksignore");
  writeFileSync(emptyIgnore, "");
  let status = 0;
  for (const target of targets) {
    const result = await runChild(
      binary,
      [
        "dir",
        "--redact=100",
        "--no-banner",
        "--no-color",
        "--exit-code",
        "1",
        "--ignore-gitleaks-allow",
        "--gitleaks-ignore-path",
        emptyIgnore,
        "--max-archive-depth",
        "3",
        "--max-decode-depth",
        "3",
        target,
      ],
      { cwd: scratch, env: scannerEnvironment() },
    );
    status ||= result.status === 0 ? 0 : 1;
  }
  return status;
}

/**
 * @typedef {object} ScanOptions
 * @property {() => Promise<string> | string} gitleaks Resolves the scanner executable.
 * @property {(text: string) => void} [stdout]
 * @property {(text: string) => void} [stderr]
 */

/**
 * @param {string[]} paths
 * @param {ScanOptions} options
 * @returns {Promise<number>} 0 only when every target was scanned clean and none changed meanwhile.
 */
export async function scanArtifacts(
  paths,
  {
    gitleaks,
    stdout = (text) => process.stdout.write(text),
    stderr = (text) => process.stderr.write(text),
  },
) {
  let stage = "INPUT";
  /** @type {string | undefined} */
  let scratch;
  let status = 1;
  try {
    if (paths.length === 0) {
      throw new Error("No artifact paths supplied.");
    }
    const targets = paths.map(checkTree);
    const before = targets.map(fingerprint);
    scratch = mkdtempSync(join(tmpdir(), "claimcore-artifact-scan-"));
    chmodSync(scratch, 0o700);
    stage = "SCANNER_ACQUISITION";
    const binary = await gitleaks();
    stage = "SCANNER_EXECUTION";
    status = await scanTargets(binary, targets, scratch);
    stage = "POST_SCAN_FINGERPRINT";
    if (targets.some((target, index) => fingerprint(target) !== before[index])) {
      status = 1;
    }
    if (status === 0) {
      stdout(`Artifact secret scan passed for ${targets.length} explicit path(s).\n`);
    } else {
      stderr("Artifact secret scan completed and refused its targets.\n");
    }
  } catch {
    // No exception detail, target path or tool log: the stage alone tells an operator whether the
    // scanner could not run or ran and found something.
    stderr(`Artifact secret scan could not complete at ${stage}.\n`);
    status = 1;
  } finally {
    if (scratch !== undefined) {
      rmSync(scratch, { recursive: true, force: true });
    }
  }
  return status;
}
