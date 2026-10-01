// Lints the frontend with oxlint's type-aware rules, one TypeScript project after another, and
// exits non-zero on any finding, warning or unused disable directive.
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { lintProjects } from "./lint-projects.mjs";

const root = fileURLToPath(new URL("..", import.meta.url));
const oxlint = fileURLToPath(new URL("../node_modules/oxlint/bin/oxlint", import.meta.url));

let failed = false;
for (const project of lintProjects) {
  const run = spawnSync(
    process.execPath,
    [
      oxlint,
      "--config",
      ".oxlintrc.json",
      "--type-aware",
      "--tsconfig",
      project.tsconfig,
      ...project.paths,
    ],
    { cwd: root, stdio: "inherit" },
  );
  if (run.status !== 0) {
    process.stderr.write(`oxlint failed for ${project.tsconfig} (${run.status ?? run.signal}).\n`);
    failed = true;
  }
}
process.exitCode = failed ? 1 : 0;
