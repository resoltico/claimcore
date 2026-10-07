import assert from "node:assert/strict";

/** @template T @param {T} value */
const normalize = (value) =>
  typeof value === "string" ? value.replace(/^\$\{\{\s*|\s*\}\}$/gu, "").trim() : value;
/** @param {import("./types.mjs").Json} step */
const upload = (step) => step.uses?.startsWith("actions/upload-artifact@");
/**
 * @param {string} value
 * @param {boolean} glob Whether a glob is allowed.
 */
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
    !/\$\{\{(?!\s*(?:github\.run_id|github\.run_attempt|matrix\.(?:suite|platform|engine))\s*\}\})/u.test(
      path,
    ),
    "Unreviewed artifact expression.",
  );
  if (!glob) {
    assert(!/[*?[]/u.test(path), "Scan paths cannot be globs.");
  }
  return path;
}
/** @param {string} raw @param {import("./types.mjs").Json} env */
function scanPath(raw, env) {
  const variable = /^"\$([A-Z][A-Z0-9_]*)"$/u.exec(raw.trim());
  if (variable === null) {
    return safePath(raw, false);
  }
  const [, name] = variable;
  assert.ok(name && Object.hasOwn(env, name), "Scan variable must have an explicit step binding.");
  const value = env[name];
  assert.ok(
    typeof value === "string" && !/[\r\n"']/u.test(value),
    "Scan variable must name one literal path.",
  );
  return safePath(value, false);
}
/** @param {import("./types.mjs").Json} step */
function scanPaths(step) {
  assert.equal(step.shell, "bash");
  assert.equal(normalize(step.if), "always()");
  const lines = step.run
    .split(/\r?\n/u)
    .map((/** @type {string} */ line) => line.trim().replace(/\\$/u, "").trim())
    .filter(Boolean);
  const prefix = "node eng/ci/scan/main.mjs artifacts";
  assert(
    lines[0] === prefix || lines[0].startsWith(`${prefix} `),
    "Scan must invoke the private-output policy directly.",
  );
  const values = lines[0] === prefix ? lines.slice(1) : [lines[0].slice(prefix.length)];
  assert(values.length && (lines[0] === prefix || lines.length === 1), "Unexpected scan command.");
  return values.map((/** @type {string} */ path) => scanPath(path, step.env ?? {}));
}
/** Mandatory scan protection can be strengthened by earlier successful evidence steps. @param {import("./types.mjs").Json} step @param {import("./types.mjs").Json[]} before */
function uploadGuard(step, before) {
  const condition = normalize(step.if);
  assert.ok(typeof condition === "string");
  const clauses = condition.split(/\s*&&\s*/u);
  assert.deepEqual(clauses.slice(0, 2), ["always()", "steps.artifact_scan.outcome == 'success'"]);
  const names = new Set();
  for (const clause of clauses.slice(2)) {
    const match = /^steps\.([a-zA-Z_][a-zA-Z0-9_-]*)\.outcome == 'success'$/u.exec(clause);
    assert.ok(match && match[1] !== "artifact_scan");
    assert.ok(
      before.some((candidate) => candidate.id === match[1]),
      "Evidence guard must name an earlier step.",
    );
    assert.ok(!names.has(match[1]), "Duplicate evidence guard.");
    names.add(match[1]);
  }
}
/**
 * Every artifact upload must follow exactly one successful scan that covers its paths.
 * @param {import("./types.mjs").Json[]} steps
 */
export function checkUploads(steps) {
  const uploaded = steps.filter(upload);
  if (!uploaded.length) {
    return;
  }
  const scans = steps.filter((step) => step.id === "artifact_scan");
  assert.equal(scans.length, 1, "Uploads require one artifact scan.");
  const [scan] = scans;
  assert(scan, "Uploads require one artifact scan.");
  assert(
    scan["continue-on-error"] === undefined || scan["continue-on-error"] === false,
    "A failed scan must fail its job.",
  );
  const paths = scanPaths(scan);
  const after = steps.slice(steps.indexOf(scan) + 1);
  assert(
    after.length === uploaded.length && after.every(upload),
    "No producer or other step may run after artifact scanning.",
  );
  for (const step of uploaded) {
    uploadGuard(step, steps.slice(0, steps.indexOf(scan)));
    assert.equal(step.with?.["if-no-files-found"], "error");
    assert(
      step.with["include-hidden-files"] !== true && step.with.overwrite !== true,
      "Unreviewed upload overwrite/hidden data.",
    );
    for (const raw of step.with.path.trim().split(/\r?\n/u)) {
      const path = safePath(raw, true);
      const glob = path.search(/[*?[]/u);
      const base = glob < 0 ? path : path.slice(0, path.lastIndexOf("/", glob));
      assert(
        paths.some(
          (/** @type {string} */ prefix) => base === prefix || base.startsWith(`${prefix}/`),
        ),
        "Upload is not covered by the scan.",
      );
    }
  }
}
