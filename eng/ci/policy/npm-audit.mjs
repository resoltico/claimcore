// Preserve full npm audit coverage while upstream has no patched release for two build-only
// dependencies. Every indirect finding must resolve to one of these exact advisory roots.
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

const root = fileURLToPath(new URL("../../..", import.meta.url));
const reviewDate = "2026-11-03";
const reviewBefore = Date.parse(`${reviewDate}T00:00:00Z`);
const exceptions = new Map([
  ["braces", "https://github.com/advisories/GHSA-vfj7-8cjw-p6xm"],
  ["http-cache-semantics", "https://github.com/advisories/GHSA-ch52-4w7c-c8xp"],
]);

/** @param {unknown} report */
function vulnerabilities(report) {
  if (report === null || typeof report !== "object" || "error" in report) {
    throw new Error("npm audit returned an invalid result.");
  }
  const value = /** @type {Record<string, unknown>} */ (report);
  const entries = value["vulnerabilities"];
  const { metadata } = value;
  if (entries === null || typeof entries !== "object" || Array.isArray(entries)) {
    throw new Error("npm audit omitted its vulnerability graph.");
  }
  if (metadata === null || typeof metadata !== "object") {
    throw new Error("npm audit omitted its summary.");
  }
  const counts = /** @type {Record<string, unknown>} */ (metadata)["vulnerabilities"];
  if (counts === null || typeof counts !== "object") {
    throw new Error("npm audit omitted its vulnerability count.");
  }
  const graph = /** @type {Record<string, unknown>} */ (entries);
  if (/** @type {Record<string, unknown>} */ (counts)["total"] !== Object.keys(graph).length) {
    throw new Error("npm audit's count and graph disagree.");
  }
  return graph;
}

/** @param {unknown} full @param {unknown} production @param {number} [now] */
export function assess(full, production, now = Date.now()) {
  const all = vulnerabilities(full);
  if (Object.keys(vulnerabilities(production)).length !== 0) {
    throw new Error("Production dependencies have an audit finding.");
  }
  if (!Number.isFinite(now) || now >= reviewBefore) {
    throw new Error("Build-tool advisory exceptions require renewed review.");
  }
  const observed = new Set();

  /** @param {string} name @param {Set<string>} path */
  function check(name, path) {
    if (path.has(name)) {
      throw new Error("npm audit reported a cyclic vulnerability graph.");
    }
    const entry = all[name];
    if (entry === null || typeof entry !== "object") {
      throw new Error("npm audit reported an unresolved dependency finding.");
    }
    const { via } = /** @type {Record<string, unknown>} */ (entry);
    if (!Array.isArray(via) || via.length === 0) {
      throw new Error("npm audit reported a finding without a cause.");
    }
    const next = new Set([...path, name]);
    for (const cause of via) {
      if (typeof cause === "string") {
        check(cause, next);
      } else if (cause !== null && typeof cause === "object") {
        const advisory = /** @type {Record<string, unknown>} */ (cause);
        if (advisory["name"] !== name || advisory["url"] !== exceptions.get(name)) {
          throw new Error("npm audit reported an unreviewed advisory.");
        }
        observed.add(name);
      } else {
        throw new Error("npm audit reported a malformed cause.");
      }
    }
  }

  for (const name of Object.keys(all)) {
    check(name, new Set());
  }
  if (observed.size !== exceptions.size) {
    throw new Error("A build-tool advisory exception is stale.");
  }
  return observed.size;
}

/** @param {boolean} omitDev */
function audit(omitDev) {
  const args = ["--prefix", "web", "audit", "--json", "--audit-level=low"];
  if (omitDev) {
    args.push("--omit=dev");
  }
  const result = spawnSync("npm", args, {
    cwd: root,
    encoding: "utf8",
    timeout: 120_000,
    maxBuffer: 20_000_000,
  });
  if (result.error || ![0, 1].includes(result.status ?? -1)) {
    throw new Error("npm audit could not complete.");
  }
  try {
    return JSON.parse(result.stdout);
  } catch {
    throw new Error("npm audit returned malformed JSON.");
  }
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  try {
    const count = assess(audit(false), audit(true));
    process.stdout.write(
      `Production audit clean; ${count} reviewed build-tool advisories remain until ${reviewDate}.\n`,
    );
  } catch (error) {
    process.stderr.write(`${error instanceof Error ? error.message : "npm audit failed."}\n`);
    process.exitCode = 1;
  }
}
