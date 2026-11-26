// Lints the frontend with oxlint's type-aware rules, one TypeScript project after another, and
// stops at the first project with a finding, warning, unused disable directive or tool failure.
import { resolve, sep } from "node:path";
import { readFileSync, readdirSync } from "node:fs";
import { taskDefinitions, endpoints } from "../../contracts/client.ts";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { themeCss } from "./generate-theme.mjs";
import { designFindings } from "./design-lint.mjs";
import { lintProjects } from "./lint-projects.mjs";

const root = fileURLToPath(new URL("..", import.meta.url));
const oxlint = fileURLToPath(new URL("../node_modules/oxlint/bin/oxlint", import.meta.url));

// Core declarations own task tokens; endpoint bindings belong to the transport and routing adapters.
/** @type {Set<string>} */
const operationIdentifiers = new Set(
  taskDefinitions.filter(({ kind }) => kind === "OPERATION").map(({ id }) => id),
);
/** @type {Set<string>} */
const endpointIdentifiers = new Set(endpoints.map(({ id }) => id));
const endpointAdapters = new Set(["routing/coreLinks.ts", "api/service.ts", "protocol/types.ts"]);
const identifierFindings = [];
for (const path of readdirSync(resolve(root, "src"), { recursive: true, encoding: "utf8" })) {
  if (!/\.(css|tsx?)$/u.test(path)) {
    continue;
  }
  const source = readFileSync(resolve(root, "src", path), "utf8");
  // Read quoted tokens while skipping comments; URL strings remain whole literals.
  for (const match of source.matchAll(
    /\/\/[^\n]*|\/\*[\s\S]*?\*\/|(["'\x60])((?:\\.|(?!\1)[^\r\n])*)\1/gu,
  )) {
    if (
      match[2] !== undefined &&
      (operationIdentifiers.has(match[2]) ||
        (endpointIdentifiers.has(match[2]) && !endpointAdapters.has(path.split(sep).join("/"))))
    ) {
      const line = source.slice(0, match.index).split("\n").length;
      identifierFindings.push(
        `${path}:${line}: task/endpoint token ${
          match[2]
        } must come from Core declarations or the transport adapter.`,
      );
    }
  }
}
if (identifierFindings.length > 0) {
  process.stderr.write(`${identifierFindings.join("\n")}\n`);
  process.exit(1);
}

const findings = designFindings(root, themeCss);
if (findings.length > 0) {
  process.stderr.write(`${findings.join("\n")}\n`);
  process.exit(1);
}

const completion = spawnSync(
  process.execPath,
  [oxlint, "--config", "test-completion-lint.config.ts", "tests", "e2e"],
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
