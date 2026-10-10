import { commandLine, minimumPowerShellMajor, powerShellHost } from "./executable.mjs";
// Report whether this machine has the toolchain a checkout pins, and how to fix what is missing.
// Every version is read from the file that owns it: global.json, .node-version, the package
// manifests' engines and config/tools.json. Exit status 0 means every required tool matches.
//
//   node eng/ci/doctor.mjs
import { spawnSync } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { createRequire } from "node:module";
import { installedTool, loadTools, pathWithTools, platformKey } from "./tools.mjs";

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
 * @returns {string | undefined} A recognized version token, or undefined when it cannot run.
 */
function capture(command, args) {
  let result;
  try {
    result = spawnSync(...commandLine(command, args), {
      cwd: root,
      encoding: "utf8",
      env: { ...process.env, PATH: pathWithTools(root) },
      timeout: 10_000,
      maxBuffer: 4096,
    });
  } catch {
    return undefined;
  }
  if (result.status !== 0) {
    return undefined;
  }
  return reportedVersion(command, result.stdout);
}

/** Availability probes may have distribution suffixes; pinned probes keep exact core tokens.
 * @param {string} command @param {string} output @returns {string | undefined} */
export function reportedVersion(command, output) {
  const pattern =
    command === "git"
      ? /^git version (\d+\.\d+\.\d+(?:\.windows\.\d+)?)(?:\s|$)/u
      : /(?:^|\s)v?(\d+\.\d+\.\d+)(?:[\s,]|$)/u;
  return output.trim().match(pattern)?.[1] ?? undefined;
}

/** @param {string} path @returns {any} */
const readJson = (path) => JSON.parse(readFileSync(join(root, path), "utf8"));

/** @returns {Check[]} */
function powerShellChecks() {
  if (process.platform !== "win32") {
    return [];
  }
  let actual;
  try {
    actual = powerShellHost().version;
  } catch {
    actual = undefined;
  }
  return [
    {
      tool: "pwsh",
      expected: `${minimumPowerShellMajor}+`,
      actual,
      required: true,
      fix: `Install PowerShell ${minimumPowerShellMajor}+ at the standard machine MSI location.`,
    },
  ];
}

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
function nativeChecks() {
  const commands = [
    "bash",
    "shellcheck",
    "jq",
    "curl",
    "openssl",
    ...(process.platform === "win32" ? [] : ["cc"]),
  ];
  return commands
    .map((tool) => {
      const result = spawnSync(
        ...commandLine(tool, [tool === "openssl" ? "version" : "--version"]),
        {
          cwd: root,
          stdio: "ignore",
          timeout: 10_000,
        },
      );
      return {
        tool,
        expected: "any",
        actual: result.status === 0 ? "available" : undefined,
        required: tool === "cc",
        fix: `Install ${tool} for ${tool === "cc" ? "native builds" : "quality and qualification"}.`,
      };
    })
    .concat([
      {
        tool: "docker-daemon",
        expected: "any",
        actual: capture("docker", ["info", "--format", "{{.ServerVersion}}"]),
        required: false,
        fix: "Start Docker and select a reachable context for PostgreSQL/published qualification.",
      },
    ]);
}

/** Availability only; no browser execution or download.
 * @returns {Check[]} */
function browserChecks() {
  let browsers;
  try {
    browsers = createRequire(join(root, "web/package.json"))("@playwright/test");
  } catch {
    browsers = {};
  }
  return ["chromium", "firefox", "webkit"].map((tool) => ({
    tool,
    expected: "any",
    actual: browsers[tool] && existsSync(browsers[tool].executablePath()) ? "available" : undefined,
    required: false,
    fix: `Run the pinned Playwright install for ${tool}; availability does not qualify browser behavior.`,
  }));
}

/** @returns {Check[]} */
export function collectChecks() {
  const platform = platformKey();
  const pinned = Object.entries(loadTools(root)).map(([name, tool]) => ({
    tool: name,
    expected: tool.version,
    actual: installedTool(root, name) === null ? undefined : tool.version,
    required: false,
    fix: tool.assets[platform]
      ? `Run: node eng/ci/tools.mjs ${name}`
      : `No pinned ${name} asset exists for ${platform}; install ${tool.version} yourself.`,
  }));
  return [
    ...platformChecks(),
    ...powerShellChecks(),
    ...nativeChecks(),
    ...browserChecks(),
    ...pinned,
  ];
}

/**
 * @param {Check} check
 * @returns {boolean}
 */
const satisfied = ({ tool, expected, actual }) =>
  actual !== undefined &&
  (expected === "any" ||
    actual === expected ||
    (tool === "pwsh" && Number(actual.split(".")[0]) >= minimumPowerShellMajor));

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
  process.stdout.write(
    "Availability is not executed qualification. Optional gaps leave the corresponding quality, PostgreSQL, published or browser family unavailable.\n",
  );
  process.exitCode = checks.some((check) => check.required && !satisfied(check)) ? 1 : 0;
}
