// Locked restore/build of the projects a selected suite actually needs.
import { spawnSync } from "node:child_process";
import { executable } from "../executable.mjs";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { join, relative } from "node:path";
import { artifactDirectory } from "../artifact-path.mjs";
import { resolveSourceFile } from "../repository-path.mjs";

// A partial solution must keep the same configuration propagation as a direct project build.
const parentConfiguration = "-p:ShouldUnsetParentConfigurationAndPlatform=false";

/** @param {import("./registry.mjs").Suite[]} suites */
export function buildInputs(suites) {
  const inputs = suites.flatMap((suite) => [
    ...(suite.build ?? []).map((project) => ({
      project,
      configuration: suite.configuration ?? "Release",
      msbuild: [],
    })),
    {
      project: suite.project ?? "",
      configuration: suite.configuration ?? "Release",
      msbuild: suite.msbuild ?? [],
    },
  ]);
  return [...new Map(inputs.map((input) => [JSON.stringify(input), input])).values()];
}

/** @param {import("./registry.mjs").Suite[]} suites */
export function buildGroups(suites) {
  /** @type {Map<string, {configuration: string, msbuild: string[], projects: string[]}>} */
  const groups = new Map();
  for (const input of buildInputs(suites)) {
    const key = JSON.stringify([input.configuration, input.msbuild]);
    const group = groups.get(key) ?? {
      configuration: input.configuration,
      msbuild: input.msbuild,
      projects: [],
    };
    group.projects.push(input.project);
    groups.set(key, group);
  }
  return [...groups.values()];
}

/** @param {string} value */
function xmlAttribute(value) {
  return value
    .replaceAll("&", "&amp;")
    .replaceAll('"', "&quot;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;");
}

/** @param {string} root @param {string} directory @param {string[]} projects */
export function solutionText(root, directory, projects) {
  const entries = projects.map((project) => {
    const path = relative(directory, resolveSourceFile(root, project)).replaceAll("\\", "/");
    return `  <Project Path="${xmlAttribute(path)}" />`;
  });
  return `<Solution>\n${entries.join("\n")}\n</Solution>\n`;
}

/** @param {string} root @param {string[]} args */
function checked(root, args) {
  const result = spawnSync(executable("dotnet"), args, { cwd: root, stdio: "inherit" });
  if (result.status !== 0) {
    throw new Error("A required locked .NET restore or build failed.");
  }
}

/** @param {string} root @param {import("./registry.mjs").Suite[]} suites @param {{ restore?: boolean }} [options] */
export function buildSuites(root, suites, { restore = true } = {}) {
  const parent = artifactDirectory(root, "artifacts/suite-build");
  mkdirSync(parent, { recursive: true, mode: 0o700 });
  const directory = mkdtempSync(join(parent, "requirements-"));
  let passed = false;
  try {
    const groups = buildGroups(suites).map((group, index) => {
      const solution = join(directory, `requirements-${index}.slnx`);
      writeFileSync(solution, solutionText(root, directory, group.projects), { mode: 0o600 });
      return { configuration: group.configuration, msbuild: group.msbuild, solution };
    });
    if (restore) {
      for (const group of groups) {
        checked(root, [
          "restore",
          group.solution,
          "--locked-mode",
          `-p:Configuration=${group.configuration}`,
          parentConfiguration,
          ...group.msbuild,
        ]);
      }
    }
    for (const group of groups) {
      checked(root, [
        "build",
        group.solution,
        "--configuration",
        group.configuration,
        "--no-restore",
        "--disable-build-servers",
        "-maxcpucount:1",
        parentConfiguration,
        ...group.msbuild,
      ]);
    }
    passed = true;
  } finally {
    if (passed) {
      rmSync(directory, { recursive: true });
    } else {
      process.stderr.write(`Failed suite build requirements retained: ${directory}.\n`);
    }
  }
}
