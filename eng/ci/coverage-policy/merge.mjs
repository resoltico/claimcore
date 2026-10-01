import { executable } from "../executable.mjs";
// Merge the Cobertura reports of every measured process and enforce the coverage floors.
import { appendFileSync, existsSync, mkdirSync, readFileSync } from "node:fs";
import { dirname, isAbsolute, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { spawnSync } from "node:child_process";
import { loadSuites } from "../suites/registry.mjs";
import { checkFloors, resolveInputs } from "./policy.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");

/**
 * @param {string} path
 * @returns {string}
 */
const inRepository = (path) => (isAbsolute(path) ? resolve(path) : resolve(root, path));

/**
 * @param {string} inputRoot
 * @param {string} outputRoot
 * @param {string | undefined} summaryPath
 * @returns {string} The one-line verdict.
 */
export function mergeCoverage(inputRoot, outputRoot, summaryPath) {
  const output = inRepository(outputRoot);
  const artifacts = join(root, "artifacts");
  if (relative(artifacts, output).startsWith("..") || output === artifacts) {
    throw new Error("Merged coverage output must be a new directory under artifacts/.");
  }
  if (existsSync(output)) {
    throw new Error("The merged coverage output directory must start absent.");
  }
  const reports = resolveInputs(inRepository(inputRoot), loadSuites(root));
  mkdirSync(output, { recursive: true });
  const generated = spawnSync(
    executable("dotnet"),
    [
      "tool",
      "run",
      "reportgenerator",
      "--",
      `-reports:${reports.join(";")}`,
      `-targetdir:${output}`,
      "-reporttypes:Cobertura;MarkdownSummaryGithub",
      "-title:ClaimCore coverage",
    ],
    { cwd: root, stdio: "inherit" },
  );
  if (generated.status !== 0) {
    throw new Error("The pinned report generator failed.");
  }
  const assessed = checkFloors(join(output, "Cobertura.xml"));
  if (summaryPath !== undefined) {
    appendFileSync(summaryPath, readFileSync(join(output, "SummaryGithub.md"), "utf8"));
  }
  const percent = (/** @type {number} */ value) => (value * 100).toFixed(2);
  return `Merged production coverage passed: line ${percent(assessed.lineRate)}%, branch ${percent(assessed.branchRate)}%, Web packages ${assessed.webPackages}.`;
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const [inputRoot, outputRoot, summaryPath] = process.argv.slice(2);
  if (inputRoot === undefined || outputRoot === undefined) {
    process.stderr.write("usage: merge.mjs <input-root> <output-root> [summary-file]\n");
    process.exit(2);
  }
  try {
    process.stdout.write(`${mergeCoverage(inputRoot, outputRoot, summaryPath)}\n`);
  } catch (error) {
    process.stderr.write(
      `Merged coverage failed: ${error instanceof Error ? error.message : String(error)}\n`,
    );
    process.exit(1);
  }
}
