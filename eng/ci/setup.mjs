// Prepare a fresh checkout: restore the locked dependency graphs, the pinned local tools and the
// pinned downloadable tools, generate the contract artifacts, then build.
//
//   node eng/ci/setup.mjs
import { spawnSync } from "node:child_process";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../..");

/** @type {string[][]} */
const steps = [
  ["dotnet", "restore", "ClaimCore.slnx", "--locked-mode"],
  ["dotnet", "tool", "restore"],
  ["npm", "--prefix", "eng", "ci"],
  ["npm", "--prefix", "web", "ci"],
  ["node", "eng/ci/tools.mjs"],
  ["npm", "--prefix", "web", "run", "contract:generate"],
  ["dotnet", "build", "ClaimCore.slnx", "--configuration", "Release", "--no-restore"],
];

for (const [command = "", ...args] of steps) {
  process.stdout.write(`> ${[command, ...args].join(" ")}\n`);
  const result = spawnSync(command, args, { cwd: root, stdio: "inherit" });
  if (result.status !== 0) {
    process.stderr.write(`Setup stopped: ${command} ${args[0] ?? ""} failed.\n`);
    process.exit(result.status ?? 1);
  }
}
