import assert from "node:assert/strict";

/**
 * @param {unknown} value
 * @returns {bigint[]}
 */
export function version(value) {
  assert(
    typeof value === "string" && /^(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)$/u.test(value),
    "Unsupported dependency version metadata.",
  );
  return value.split(".").map(BigInt);
}
/** @param {string} left @param {string} right */
export function newer(left, right) {
  const a = version(left);
  const b = version(right);
  for (let i = 0; i < 3; i += 1) {
    if (a[i] !== b[i]) return (a[i] ?? 0n) > (b[i] ?? 0n);
  }
  return false;
}
/** @param {string} ecosystem @param {string} name */
export const packageKey = (ecosystem, name) =>
  `${ecosystem}|${ecosystem === "nuget" ? name.toLowerCase() : name}`;

/**
 * The packages one NuGet project lists in the named collections.
 * @param {import("./types.mjs").Json} project
 * @param {string[]} collections
 * @returns {import("./types.mjs").Json[]}
 */
function projectRows(project, collections) {
  assert(project && typeof project === "object", "Malformed NuGet project metadata.");
  assert(!project.errors?.length && !project.problems?.length, "NuGet project metadata failed.");
  if (project.frameworks === undefined) return [];
  assert(Array.isArray(project.frameworks), "Malformed framework metadata.");
  return project.frameworks.flatMap((/** @type {import("./types.mjs").Json} */ framework) =>
    collections.flatMap((collection) => {
      const values = framework[collection] ?? [];
      assert(Array.isArray(values), "Malformed package collection.");
      return values;
    }),
  );
}

/**
 * @param {import("./types.mjs").Json} document A `dotnet package list --format json` document.
 * @param {string[]} collections
 * @returns {import("./types.mjs").Json[]}
 */
export function packageRows(document, collections) {
  assert(
    document && Array.isArray(document.projects) && document.projects.length > 0,
    "Incomplete NuGet metadata response.",
  );
  assert(!document.errors?.length && !document.problems?.length, "NuGet reported metadata errors.");
  return document.projects.flatMap((/** @type {import("./types.mjs").Json} */ project) =>
    projectRows(project, collections),
  );
}

/**
 * @typedef {object} Finding
 * @property {string} ecosystem
 * @property {string} package
 * @property {string} current
 * @property {string} [latest]
 * @property {string} kind
 * @property {boolean} [held]
 */

/**
 * @param {string} ecosystem
 * @param {unknown} name
 * @param {string} current
 * @param {string | undefined} latest
 * @param {Map<string, Set<string>>} installed
 * @param {string} kind
 * @returns {Finding}
 */
export function safeFinding(ecosystem, name, current, latest, installed, kind) {
  assert(
    typeof name === "string" && name.length <= 200 && /^@?[A-Za-z0-9][A-Za-z0-9_./-]*$/u.test(name),
    "Invalid package identity.",
  );
  assert(
    installed.get(packageKey(ecosystem, name))?.has(current),
    "Package metadata does not match the locked graph.",
  );
  version(current);
  if (latest !== undefined) version(latest);
  return {
    ecosystem,
    package: name,
    current,
    ...(latest === undefined ? {} : { latest }),
    kind,
  };
}

/**
 * @param {import("./types.mjs").Json} document
 * @param {Map<string, Set<string>>} installed
 * @param {string} [today]
 * @returns {Set<string>}
 */
export function validateHolds(document, installed, today = new Date().toISOString().slice(0, 10)) {
  assert(
    document?.version === 1 && Array.isArray(document.holds),
    "Invalid dependency-hold registry.",
  );
  const keys = new Set();
  for (const hold of document.holds) {
    assert(["nuget", "npm"].includes(hold.ecosystem), "Invalid hold ecosystem.");
    safeFinding(hold.ecosystem, hold.package, hold.current, hold.latest, installed, "hold");
    assert(hold.current !== hold.latest, "A hold must identify an available alternative.");
    assert(
      typeof hold.owner === "string" && hold.owner.trim().length >= 3,
      "A hold requires its owner.",
    );
    assert(
      typeof hold.rationale === "string" && hold.rationale.trim().length >= 20,
      "A hold requires substantive rationale.",
    );
    const dates = [hold.reviewOn, hold.expiresOn].filter((date) => date !== undefined);
    assert(dates.length > 0, "A hold requires a review or expiry date.");
    for (const date of dates) {
      assert(
        typeof date === "string" &&
          /^\d{4}-\d{2}-\d{2}$/u.test(date) &&
          !Number.isNaN(Date.parse(date)) &&
          new Date(date).toISOString().slice(0, 10) === date,
        "Invalid hold date.",
      );
      assert(date >= today, "A dependency hold needs review or has expired.");
    }
    const key = `${packageKey(hold.ecosystem, hold.package)}|${hold.current}|${hold.latest}`;
    assert(!keys.has(key), "Duplicate dependency hold.");
    keys.add(key);
  }
  return keys;
}

/** @param {Finding[]} findings @param {Set<string>} holds */
export function classifyUpdates(findings, holds) {
  return findings.map((finding) => ({
    ...finding,
    held: holds.has(
      `${packageKey(finding.ecosystem, finding.package)}|${finding.current}|${finding.latest}`,
    ),
  }));
}
