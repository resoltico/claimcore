// Update and vulnerability findings for the locked NuGet and npm graphs.
import { join } from "node:path";
import { jsonProcess } from "./dependency-process.mjs";
import { newer, packageRows, safeFinding } from "./dependency-policy.mjs";

const collections = ["topLevelPackages", "transitivePackages"];

/**
 * @param {string} root
 * @param {string} flag A `dotnet package list` selector such as `--outdated`.
 */
function nuget(root, flag) {
  return jsonProcess(
    "dotnet",
    [
      "package",
      "list",
      "--project",
      join(root, "ClaimCore.slnx"),
      "--format",
      "json",
      "--output-version",
      "1",
      "--no-restore",
      flag,
      "--include-transitive",
    ],
    root,
  );
}

/**
 * NuGet packages flagged vulnerable or deprecated.
 * @param {string} root
 * @param {import("./dependency-inventory.mjs").Installed} installed
 * @returns {import("./dependency-policy.mjs").Finding[]}
 */
export function nugetSecurityFindings(root, installed) {
  return ["--vulnerable", "--deprecated"].flatMap((flag) =>
    packageRows(nuget(root, flag), collections).map((item) =>
      safeFinding("nuget", item.id, item.resolvedVersion, undefined, installed, flag.slice(2)),
    ),
  );
}

/**
 * NuGet packages with a newer release.
 * @param {string} root
 * @param {import("./dependency-inventory.mjs").Installed} installed
 * @returns {import("./dependency-policy.mjs").Finding[]}
 */
export function nugetUpdateFindings(root, installed) {
  return packageRows(nuget(root, "--outdated"), collections).map((item) =>
    safeFinding("nuget", item.id, item.resolvedVersion, item.latestVersion, installed, "update"),
  );
}

/**
 * The newest release published within the installed major version.
 * @param {string} name
 * @param {string} current
 * @param {string} project
 */
function newestInMajor(name, current, project) {
  const published = jsonProcess(
    "npm",
    ["view", `${name}@${current.split(".")[0]}`, "version", "--json"],
    project,
  );
  const versions = /** @type {string[]} */ (Array.isArray(published) ? published : [published]);
  if (versions.length === 0) throw new Error("DEPENDENCY_METADATA_EMPTY");
  return versions.reduce((a, b) => (newer(a, b) ? a : b));
}

/**
 * npm packages of one project with a newer release, or installed ahead of the tagged one.
 * @param {string} project Directory of the npm project.
 * @param {import("./dependency-inventory.mjs").Installed} installed
 * @returns {import("./dependency-policy.mjs").Finding[]}
 */
export function npmUpdateFindings(project, installed) {
  const outdated = jsonProcess("npm", ["outdated", "--json"], project, [0, 1]);
  if (!outdated || typeof outdated !== "object" || Array.isArray(outdated))
    throw new Error("DEPENDENCY_METADATA_INVALID");
  /** @type {import("./dependency-policy.mjs").Finding[]} */
  const findings = [];
  for (const [name, value] of Object.entries(
    /** @type {Record<string, { current: string, latest: string }>} */ (outdated),
  )) {
    if (newer(value.latest, value.current)) {
      findings.push(safeFinding("npm", name, value.current, value.latest, installed, "update"));
    } else if (newer(value.current, value.latest)) {
      const latest = newestInMajor(name, value.current, project);
      if (newer(latest, value.current))
        findings.push(safeFinding("npm", name, value.current, latest, installed, "update"));
    }
  }
  return findings;
}
