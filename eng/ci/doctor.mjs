import { commandLine } from "./executable.mjs";
// Report whether this machine has the toolchain a checkout pins, and how to fix what is missing.
// Every version is read from the file that owns it: global.json, .node-version, the package
// manifests' engines and config/tools.json. Exit status 0 means every required tool matches.
//
//   node eng/ci/doctor.mjs
import { spawnSync } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { loadTools, pathWithTools, platformKey } from "./tools.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../..");

/**
 * @typedef {object} Check
 * @property {string} tool
 * @property {string} expected
 * @property {string | undefined} actual
 * @property {boolean} required
 * @property {string} fix
 */

/**
 * @param {string} command
 * @param {string[]} args
 * @returns {string | undefined} Trimmed standard output, or undefined when it cannot run.
 */
function capture(command, args) {
  const result = spawnSync(...commandLine(command, args), {
    cwd: root,
    encoding: "utf8",
    env: { ...process.env, PATH: pathWithTools(root) },
  });
  return result.status === 0 ? result.stdout.trim() : undefined;
}

/** @param {string} path @returns {any} */
const readJson = (path) => JSON.parse(readFileSync(join(root, path), "utf8"));

/** @returns {Check[]} The toolchain every checkout needs. */
function platformChecks() {
  return [
    {
      tool: "dotnet",
      expected: readJson("global.json").sdk.version,
      actual: capture("dotnet", ["--version"]),
      required: true,
      fix: "Install the .NET SDK named by global.json.",
    },
    {
      tool: "node",
      expected: readFileSync(join(root, ".node-version"), "utf8").trim(),
      actual: capture("node", ["--version"])?.replace(/^v/u, ""),
      required: true,
      fix: "Install the Node release named by .node-version.",
    },
    {
      tool: "npm",
      expected: readJson("web/package.json").engines.npm,
      actual: capture("npm", ["--version"]),
      required: true,
      fix: "Install the npm release named by web/package.json engines.",
    },
    {
      tool: "git",
      expected: "any",
      actual: capture("git", ["--version"]),
      required: true,
      fix: "Install Git.",
    },
    {
      tool: "docker",
      expected: "any",
      actual: capture("docker", ["--version"]),
      required: false,
      fix: "Install Docker to run the database suites and the published-application drills.",
    },
  ];
}

/** @returns {Check[]} */
export function collectChecks() {
  const platform = platformKey();
  const pinned = Object.entries(loadTools(root)).map(([name, tool]) => ({
    tool: name,
    expected: tool.version,
    actual: existsSync(join(root, "artifacts/tools/bin", `.${name}.json`))
      ? tool.version
      : undefined,
    required: false,
    fix: tool.assets[platform]
      ? `Run: node eng/ci/tools.mjs ${name}`
      : `No pinned ${name} asset exists for ${platform}; install ${tool.version} yourself.`,
  }));
  return [...platformChecks(), ...pinned];
}

/**
 * @param {Check} check
 * @returns {boolean}
 */
const satisfied = ({ expected, actual }) =>
  actual !== undefined && (expected === "any" || actual === expected || actual.includes(expected));

/**
 * @param {Check} check
 * @returns {string}
 */
function markFor(check) {
  if (satisfied(check)) {
    return "ok";
  }
  return check.required ? "MISSING" : "absent";
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const checks = collectChecks();
  for (const check of checks) {
    const mark = markFor(check);
    const found = check.actual ?? "not found";
    process.stdout.write(
      `${mark.padEnd(8)} ${check.tool.padEnd(12)} wants ${check.expected}, has ${found}\n`,
    );
    if (!satisfied(check)) {
      process.stdout.write(`         ${check.fix}\n`);
    }
  }
  process.exitCode = checks.some((check) => check.required && !satisfied(check)) ? 1 : 0;
}
