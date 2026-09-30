// Reports the locked dependency graph's vulnerabilities (`security`) or available updates (`health`).
//   node eng/ci/dependency-check.mjs security|health [holds registry path]
import { appendFileSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { randomUUID } from "node:crypto";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { readNpmLock, readNuGetLocks } from "./dependency-inventory.mjs";
import { classifyUpdates, validateHolds } from "./dependency-policy.mjs";
import {
  nugetSecurityFindings,
  nugetUpdateFindings,
  npmUpdateFindings,
} from "./dependency-updates.mjs";

const root = fileURLToPath(new URL("../..", import.meta.url));
const npmProjects = ["web", "eng"];
const { argv } = process;
const [, , mode] = argv;
if ((mode !== "security" && mode !== "health") || argv.length > 4) {
  throw new Error("Expected security or health and optional registry path.");
}
const inCi = process.env["GITHUB_ACTIONS"] === "true";
const [ciOutput, localOutput] =
  mode === "health"
    ? ["artifacts/dependency-health", `artifacts/dependency-health/local-${randomUUID()}`]
    : [
        "artifacts/diagnostics/quality",
        `artifacts/diagnostics/local-dependency-security-${randomUUID()}`,
      ];
const output = inCi ? ciOutput : localOutput;
const file = mode === "health" ? "report.json" : "dependency-security.details.json";

/**
 * @typedef {object} Result
 * @property {number} schemaVersion
 * @property {string} mode
 * @property {string} outcome
 * @property {import("./dependency-policy.mjs").Finding[]} findings
 * @property {string} checkedUtc
 * @property {Record<string, string>} [npmLockSha256] Lock file hash per npm project.
 * @property {number} [findingCount]
 * @property {string} [error]
 */

/** @type {Result} */
const result = {
  schemaVersion: 2,
  mode,
  outcome: "metadata-error",
  findings: [],
  checkedUtc: new Date().toISOString(),
};

/** @param {import("./dependency-inventory.mjs").Installed} installed */
function evaluate(installed) {
  const holds = validateHolds(
    JSON.parse(readFileSync(argv[3] ?? join(root, "config/dependency-holds.json"), "utf8")),
    installed,
  );
  if (mode === "security") {
    result.findings = nugetSecurityFindings(root, installed);
    result.outcome = result.findings.length ? "security-refused" : "passed";
    return;
  }
  const npm = npmProjects.flatMap((project) => npmUpdateFindings(join(root, project), installed));
  result.findings = classifyUpdates([...nugetUpdateFindings(root, installed), ...npm], holds);
  result.outcome = result.findings.some((item) => !item.held) ? "updates-available" : "passed";
}

try {
  /** @type {import("./dependency-inventory.mjs").Installed} */
  const installed = new Map();
  for (const directory of ["src", "tests", "eng"]) {
    readNuGetLocks(join(root, directory), installed);
  }
  result.npmLockSha256 = Object.fromEntries(
    npmProjects.map((project) => [
      project,
      readNpmLock(join(root, project, "package-lock.json"), installed),
    ]),
  );
  evaluate(installed);
} catch (error) {
  // No registry payload, credential, request URI or provider exception is published.
  result.outcome = "metadata-error";
  const message = error instanceof Error ? error.message : "";
  result.error = /^DEPENDENCY_[A-Z_]+$/u.test(message)
    ? message
    : "DEPENDENCY_POLICY_OR_METADATA_INVALID";
}
result.findings = [
  ...new Map(result.findings.map((item) => [JSON.stringify(item), item])).values(),
];
result.findingCount = result.findings.length;
result.findings = result.findings.slice(0, 100);
mkdirSync(join(root, output), { recursive: true });
writeFileSync(join(root, output, file), `${JSON.stringify(result, null, 2)}\n`, { flag: "wx" });
console.log(
  `Dependency ${mode}: ${result.outcome}; ${result.findingCount} findings. Report: ${output}/${file}`,
);
const summary = process.env["GITHUB_STEP_SUMMARY"];
if (summary) {
  const lines = [
    `### Dependency ${mode}: ${result.outcome}`,
    "",
    "| Package | Current | Alternative | State |",
    "|---|---|---|---|",
  ];
  for (const item of result.findings) {
    lines.push(
      `| ${item.ecosystem}/${item.package} | ${item.current} | ${item.latest ?? "—"} | ${item.held ? "reviewed hold" : item.kind} |`,
    );
  }
  if (result.error) {
    lines.push(
      "",
      `Metadata/policy failure: ${result.error}. Inspect the pinned graph and approved hold dates; no automatic updates or retries of security findings.`,
    );
  }
  appendFileSync(summary, `${lines.join("\n")}\n`);
}
const exitCodes = /** @type {Record<string, number>} */ ({ passed: 0, "metadata-error": 3 });
process.exitCode = exitCodes[result.outcome] ?? 2;
