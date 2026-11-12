// Lints the frontend with oxlint's type-aware rules, one TypeScript project after another, and
// stops at the first project with a finding, warning, unused disable directive or tool failure.
import { resolve } from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { designFindings } from "./design-lint.mjs";
import { lintProjects } from "./lint-projects.mjs";

const root = fileURLToPath(new URL("..", import.meta.url));
const oxlint = fileURLToPath(new URL("../node_modules/oxlint/bin/oxlint", import.meta.url));

const theme = spawnSync(
  process.execPath,
  [resolve(root, "scripts/generate-theme.mjs"), "--check"],
  { stdio: "inherit" },
);
if (theme.status !== 0) {
  process.exit(1);
}
const findings = designFindings(root);
if (findings.length > 0) {
  process.stderr.write(`${findings.join("\n")}\n`);
  process.exit(1);
}

const completion = spawnSync(
  process.execPath,
  [oxlint, "--config", "test-completion-lint.config.ts", "tests"],
  { cwd: root, stdio: "inherit" },
);
if (completion.status !== 0) {
  process.exit(1);
}

for (const project of lintProjects) {
  const run = spawnSync(
    process.execPath,
    [
      oxlint,
      "--config",
      "oxlint.config.ts",
      "--type-aware",
      "--tsconfig",
      project.tsconfig,
      ...project.paths.map((path) => resolve(root, path)),
    ],
    { cwd: root, stdio: "inherit" },
  );
  if (run.status !== 0) {
    const failure = run.error?.message ?? run.status ?? run.signal;
    process.stderr.write(`oxlint failed for ${project.tsconfig} (${failure}).\n`);
    process.exitCode = 1;
    break;
  }
}
