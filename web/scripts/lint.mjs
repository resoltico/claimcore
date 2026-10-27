// Lints the frontend with oxlint's type-aware rules, one TypeScript project after another, and
// stops at the first project with a finding, warning, unused disable directive or tool failure.
import { existsSync, readdirSync } from "node:fs";
import { resolve } from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { lintProjects } from "./lint-projects.mjs";

const root = fileURLToPath(new URL("..", import.meta.url));
const oxlint = fileURLToPath(new URL("../node_modules/oxlint/bin/oxlint", import.meta.url));

const unitRoot = resolve(root, "tests/unit");
const orphaned = readdirSync(unitRoot, { recursive: true, encoding: "utf8" })
  .filter((path) => /\.test\.tsx?$/u.test(path))
  .filter((path) => {
    const source = resolve(root, "src", path.replace(/\.test\.tsx?$/u, ""));
    return !existsSync(`${source}.ts`) && !existsSync(`${source}.tsx`);
  });
if (orphaned.length > 0) {
  process.stderr.write(`Unit tests have no matching source module: ${orphaned.join(", ")}\n`);
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
