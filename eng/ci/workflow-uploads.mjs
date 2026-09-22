import assert from "node:assert/strict";

const normalize = (value) =>
  typeof value === "string"
    ? value.replace(/^\$\{\{\s*|\s*\}\}$/gu, "").trim()
    : value;
const upload = (step) => step.uses?.startsWith("actions/upload-artifact@");
function safePath(value, glob) {
  const path = value
    .trim()
    .replace(/^['"]|['"]$/gu, "")
    .replace(/\/$/u, "");
  assert(
    path.startsWith("artifacts/") &&
      !path.includes("\\") &&
      !path.includes("`") &&
      !path.split("/").some((part) => part === ".." || part === "."),
    "Unsafe artifact path.",
  );
  assert(
    !/\$\{\{(?!\s*(?:github\.run_id|github\.run_attempt|matrix\.(?:stage|engine))\s*\}\})/u.test(
      path,
    ),
    "Unreviewed artifact expression.",
  );
  if (!glob) assert(!/[*?\[]/u.test(path), "Scan paths cannot be globs.");
  return path;
}
function scanPaths(step) {
  assert.equal(step.shell, "pwsh");
  assert.equal(normalize(step.if), "always()");
  const lines = step.run
    .split(/\r?\n/u)
    .map((line) => line.trim().replace(/`$/u, "").trim())
    .filter(Boolean);
  const prefix = "pwsh -NoProfile -File eng/Scan-ArtifactSecrets.ps1";
  assert(
    lines[0] === prefix || lines[0].startsWith(prefix + " "),
    "Scan must invoke the private-output policy directly.",
  );
  const values =
    lines[0] === prefix ? lines.slice(1) : [lines[0].slice(prefix.length)];
  assert(
    values.length && (lines[0] === prefix || lines.length === 1),
    "Unexpected scan command.",
  );
  return values.map((path) => safePath(path, false));
}
export function checkUploads(steps) {
  const uploaded = steps.filter(upload);
  if (!uploaded.length) return;
  const scans = steps.filter((step) => step.id === "artifact_scan");
  assert.equal(scans.length, 1, "Uploads require one artifact scan.");
  const scan = scans[0];
  assert(
    scan["continue-on-error"] === undefined ||
      scan["continue-on-error"] === false,
    "A failed scan must fail its job.",
  );
  const paths = scanPaths(scan);
  const after = steps.slice(steps.indexOf(scan) + 1);
  assert(
    after.length === uploaded.length && after.every(upload),
    "No producer or other step may run after artifact scanning.",
  );
  for (const step of uploaded) {
    assert.equal(
      normalize(step.if),
      "always() && steps.artifact_scan.outcome == 'success'",
    );
    assert.equal(step.with?.["if-no-files-found"], "error");
    assert(
      step.with["include-hidden-files"] !== true &&
        step.with.overwrite !== true,
      "Unreviewed upload overwrite/hidden data.",
    );
    for (const raw of step.with.path.trim().split(/\r?\n/u)) {
      const path = safePath(raw, true);
      const glob = path.search(/[*?\[]/u);
      const base = glob < 0 ? path : path.slice(0, path.lastIndexOf("/", glob));
      assert(
        paths.some(
          (prefix) => base === prefix || base.startsWith(prefix + "/"),
        ),
        "Upload is not covered by the scan.",
      );
    }
  }
}
