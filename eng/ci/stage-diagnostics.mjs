import { relative, resolve } from "node:path";

/** @type {Record<string, string>} */
const help = {
  "restore-frontend": "Reproduce npm --prefix web ci with the pinned toolchain.",
  "frontend-format": "Run npm --prefix web run format:check; inspect the reported tracked files.",
  "frontend-types": "Run npm --prefix web run typecheck; inspect the reported compiler locations.",
  "frontend-lint": "Run npm --prefix web run lint; inspect the reported tracked files.",
  "frontend-stylelint": "Run npm --prefix web run lint:styles.",
  "frontend-dead-code": "Run npm --prefix web run dead-code.",
  "frontend-contract-inventory":
    "Run npm --prefix web run contract:check; regenerate only through the owning generator.",
  "frontend-unit":
    "Inspect the sanitized Vitest summary and reproduce npm --prefix web run test:unit.",
  "frontend-mutation":
    "Run npm --prefix web run test:mutation; inspect the local Stryker report without publishing mutant source.",
  "frontend-build":
    "Run npm --prefix web run build with the pinned tools; inspect its size and generated-asset policy.",
  "dependency-security":
    "Inspect dependency-security.details.json for locked package identities; distinguish a security finding from metadata unavailability.",
  "npm-audit": "Reproduce npm --prefix web audit --audit-level=low against the locked graph.",
  "npm-signatures":
    "Reproduce npm --prefix web audit signatures; do not bypass invalid signatures.",
  "dependency-licenses": "Run npm --prefix web run licenses:check.",
  sbom: "Run npm --prefix web run sbom.",
  fantomas: "Run bash eng/Check-Fantomas.sh; format the tracked F# sources.",
  fsharplint: "Run bash eng/Check-FSharpLint.sh; inspect the reported source locations.",
  actionlint: "Run actionlint and inspect the workflow locations.",
  "workflow-toolchain":
    "Run pwsh -File eng/Check-WorkflowToolchainPolicy.ps1; inspect structural workflow policy.",
  "workflow-toolchain-negative-controls":
    "Run pwsh -File eng/Test-WorkflowToolchainPolicy.ps1; inspect governance control failures.",
  "powershell-analysis":
    "Run pwsh -File eng/Check-PowerShellAnalysis.ps1; every PSScriptAnalyzer rule runs over the tracked sources.",
  shellcheck: "Run shellcheck over tracked eng and db shell sources.",
  shfmt: "Run shfmt -d over tracked eng and db shell sources; apply shfmt -w for the listed files.",
};
const policyStages = [
  "lint-exceptions",
  "test-suite-registry",
  "eng-tests",
  "eng-format",
  "eng-types",
  "eng-lint",
  "eng-npm-audit",
  "eng-npm-signatures",
  "python-format",
  "python-lint",
  "python-types",
  "python-limits",
  "sensitive-output-negative-controls",
  "coverage-input-negative-controls",
  "coverage-floor-negative-controls",
  "property-seed-negative-controls",
  "test-diagnostic-negative-controls",
  "convergence-assurance",
  "convergence-assurance-negative-controls",
  "compose-config",
  "compose-health",
  "docker-cleanup-assurance",
  "container-image-assurance-negative-controls",
  "container-sbom",
  "container-vulnerability-scan",
  "publish-cli",
  "publish-database",
  "publish-web",
];
for (const stage of policyStages) {
  help[stage] =
    "Reproduce the registered stage procedure with the pinned toolchain; do not waive a failed policy or evidence requirement.";
}

const escape = String.fromCharCode(27);
const ansi = new RegExp(`${escape}\\[[0-9;]*m`, "gu");
const location =
  /^(.*?)(?:\((\d+),(\d+)\)|:(\d+):(\d+)):\s*(?:error|warning)?\s*((?:FS|TS)\d{3,6})?/u;

/**
 * @typedef {object} Finding
 * @property {string} file Tracked repository-relative path.
 * @property {number} [line]
 * @property {number} [column]
 * @property {string} [rule]
 */

/**
 * @param {string} producer
 * @param {string} stage
 * @param {number[]} codes
 */
function assertIdentity(producer, stage, codes) {
  if (
    !["quality", "frontend", "frontend-product", "publish"].includes(producer) ||
    !Object.hasOwn(help, stage)
  ) {
    throw new Error("Unregistered reporting identity.");
  }
  for (const code of codes) {
    if (!Number.isSafeInteger(code) || code < 0 || code > 255) {
      throw new Error("Invalid stage exit status.");
    }
  }
}

/**
 * The tracked source location one log line names, if any.
 * @param {string} line
 * @param {string} root
 * @param {Set<string>} tracked
 * @returns {Finding | null}
 */
function findingOf(line, root, tracked) {
  const clean = line.replace(ansi, "");
  const match = location.exec(clean);
  const path = match ? (match[1] ?? "") : clean.replace(/^\[warn\]\s*/u, "").trim();
  const file = relative(resolve(root), resolve(root, path)).split("\\").join("/");
  if (!tracked.has(file)) {
    return null;
  }
  /** @type {Finding} */
  const finding = { file };
  if (!match) {
    return finding;
  }
  const lineNumber = Number(match[2] ?? match[4]);
  const column = Number(match[3] ?? match[5]);
  if (lineNumber > 0 && lineNumber <= 1000000 && column > 0 && column <= 1000000) {
    finding.line = lineNumber;
    finding.column = column;
  }
  const [, , , , , , rule] = match;
  if (rule) {
    finding.rule = rule;
  }
  return finding;
}

/**
 * @param {{ producer: string, stage: string, exitCode: number, manifestExit: number, log: string, root: string, tracked: Set<string> }} input
 */
export function stageDiagnostic({ producer, stage, exitCode, manifestExit, log, root, tracked }) {
  assertIdentity(producer, stage, [exitCode, manifestExit]);
  /** @type {Finding[]} */
  const findings = [];
  for (const line of log.slice(-2 * 1024 * 1024).split(/\r?\n/u)) {
    const finding = findingOf(line, root, tracked);
    if (finding === null) {
      continue;
    }
    if (!findings.some((item) => JSON.stringify(item) === JSON.stringify(finding))) {
      findings.push(finding);
    }
    if (findings.length === 30) {
      break;
    }
  }
  const failure = manifestExit === 0 ? "passed" : "evidence-failed";
  return {
    schemaVersion: 1,
    producer,
    stageId: stage,
    exitCode,
    manifestExit,
    outcome: exitCode === 0 ? failure : "procedure-failed",
    guidance:
      manifestExit === 0
        ? help[stage]
        : "Stage evidence did not qualify. Check required outputs and exact reviewed test inventory; a passing subprocess is insufficient.",
    findings,
    rawLogPublished: false,
  };
}
