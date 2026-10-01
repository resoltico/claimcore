// The CLI that the database suites' process tests start.
import { spawnSync } from "node:child_process";
import { existsSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");
const project = "src/ClaimCore.Cli/ClaimCore.Cli.fsproj";

/**
 * Build the CLI if it is not yet built and resolve its assembly.
 * @returns {string}
 */
export function builtCliPath() {
  const built = spawnSync(
    "dotnet",
    ["build", project, "--configuration", "Release", "--no-restore"],
    {
      cwd: root,
      stdio: "inherit",
    },
  );
  const resolved = spawnSync(
    "dotnet",
    [
      "msbuild",
      project,
      "-nologo",
      "-verbosity:quiet",
      "-property:Configuration=Release",
      "-getProperty:TargetPath",
    ],
    { cwd: root, encoding: "utf8" },
  );
  const path = resolved.stdout.trim();
  if (built.status !== 0 || resolved.status !== 0 || !existsSync(path)) {
    throw new Error("The CLI could not be built and resolved.");
  }
  return path;
}
