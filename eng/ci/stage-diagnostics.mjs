import { relative, resolve } from "node:path";

const help = {
  "restore-frontend":
    "Reproduce npm --prefix web ci with the pinned toolchain.",
  "frontend-format":
    "Run npm --prefix web run format:check; inspect the reported tracked files.",
  "frontend-types":
    "Run npm --prefix web run typecheck; inspect the reported compiler locations.",
  "frontend-eslint":
    "Run npm --prefix web run lint; inspect the reported tracked files.",
  "frontend-stylelint": "Run npm --prefix web run lint:styles.",
  "frontend-dead-code": "Run npm --prefix web run dead-code.",
  "frontend-contract-inventory":
    "Run npm --prefix web run contract:check; regenerate only through the owning generator.",
  "frontend-unit":
    "Inspect the sanitized Vitest summary and reproduce npm --prefix web run test:unit.",
  "frontend-build":
    "Run npm --prefix web run build with the pinned tools; inspect its size and generated-asset policy.",
  "dependency-security":
    "Inspect dependency-security.details.json for locked package identities; distinguish a security finding from metadata unavailability.",
  "npm-audit":
    "Reproduce npm --prefix web audit --audit-level=low against the locked graph.",
  "npm-signatures":
    "Reproduce npm --prefix web audit signatures; do not bypass invalid signatures.",
  "dependency-licenses": "Run npm --prefix web run licenses:check.",
  sbom: "Run npm --prefix web run sbom.",
  fantomas: "Run bash eng/Check-Fantomas.sh; format the tracked F# sources.",
  fsharplint:
    "Run bash eng/Check-FSharpLint.sh; inspect the reported source locations.",
  actionlint: "Run actionlint and inspect the workflow locations.",
  "workflow-toolchain":
    "Run pwsh -File eng/Check-WorkflowToolchainPolicy.ps1; inspect structural workflow policy.",
  "workflow-toolchain-negative-controls":
    "Run pwsh -File eng/Test-WorkflowToolchainPolicy.ps1; inspect governance control failures.",
  shellcheck: "Run shellcheck over tracked eng and db shell sources.",
};
const policyStages = [
  "analyzer-suppressions",
  "analyzer-suppression-negative-controls",
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
for (const stage of policyStages)
  help[stage] =
    "Reproduce the registered stage procedure with the pinned toolchain; do not waive a failed policy or evidence requirement.";

export function stageDiagnostic({
  producer,
  stage,
  exitCode,
  manifestExit,
  log,
  root,
  tracked,
}) {
  if (
    !["quality", "frontend", "publish"].includes(producer) ||
    !Object.hasOwn(help, stage)
  )
    throw new Error("Unregistered reporting identity.");
  for (const code of [exitCode, manifestExit])
    if (!Number.isSafeInteger(code) || code < 0 || code > 255)
      throw new Error("Invalid stage exit status.");
  const findings = [];
  for (const line of log.slice(-2 * 1024 * 1024).split(/\r?\n/u)) {
    const clean = line.replace(/\u001b\[[0-9;]*m/gu, "");
    const match =
      /^(.*?)(?:\((\d+),(\d+)\)|:(\d+):(\d+)):\s*(?:error|warning)?\s*((?:FS|TS)\d{3,6})?/u.exec(
        clean,
      );
    const path = match ? match[1] : clean.replace(/^\[warn\]\s*/u, "").trim();
    const candidate = relative(resolve(root), resolve(root, path))
      .split("\\")
      .join("/");
    if (!tracked.has(candidate)) continue;
    const finding = { file: candidate };
    if (match) {
      const lineNumber = Number(match[2] ?? match[4]);
      const column = Number(match[3] ?? match[5]);
      if (
        lineNumber > 0 &&
        lineNumber <= 1000000 &&
        column > 0 &&
        column <= 1000000
      )
        Object.assign(finding, { line: lineNumber, column });
      if (match[6]) finding.rule = match[6];
    }
    if (
      !findings.some((item) => JSON.stringify(item) === JSON.stringify(finding))
    )
      findings.push(finding);
    if (findings.length === 30) break;
  }
  return {
    schemaVersion: 1,
    producer,
    stageId: stage,
    exitCode,
    manifestExit,
    outcome:
      exitCode !== 0
        ? "procedure-failed"
        : manifestExit !== 0
          ? "evidence-failed"
          : "passed",
    guidance:
      manifestExit !== 0
        ? "Stage evidence did not qualify. Check required outputs and exact reviewed test inventory; a passing subprocess is insufficient."
        : help[stage],
    findings,
    rawLogPublished: false,
  };
}
