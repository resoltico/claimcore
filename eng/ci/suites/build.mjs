// Locked restore/build of the projects a selected suite actually needs.
import { spawnSync } from "node:child_process";
import { executable } from "../executable.mjs";

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

/** @param {string} root @param {string[]} args */
function checked(root, args) {
  const result = spawnSync(executable("dotnet"), args, { cwd: root, stdio: "inherit" });
  if (result.status !== 0) {
    throw new Error("A required locked .NET restore or build failed.");
  }
}

/** @param {string} root @param {import("./registry.mjs").Suite[]} suites @param {{ restore?: boolean }} [options] */
export function buildSuites(root, suites, { restore = true } = {}) {
  const inputs = buildInputs(suites);
  if (restore) {
    for (const input of inputs) {
      checked(root, [
        "restore",
        input.project,
        "--locked-mode",
        `-p:Configuration=${input.configuration}`,
        ...input.msbuild,
      ]);
    }
  }
  for (const input of inputs) {
    checked(root, [
      "build",
      input.project,
      "--configuration",
      input.configuration,
      "--no-restore",
      "--disable-build-servers",
      "-maxcpucount:1",
      ...input.msbuild,
    ]);
  }
}
