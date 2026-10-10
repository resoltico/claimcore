import { admitDotnetHost } from "./dotnet-host.mjs";
// Source restore and build commands shared by first-checkout setup and the local verification plan.
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { commandLine } from "./executable.mjs";
import { artifactDirectory } from "./artifact-path.mjs";

export const restoreCommands = [
  ["dotnet", "restore", "ClaimCore.slnx", "--locked-mode"],
  ["dotnet", "tool", "restore"],
];
export const buildCommands = [
  [
    "dotnet",
    "build",
    "ClaimCore.slnx",
    "--configuration",
    "Release",
    "--no-restore",
    "--disable-build-servers",
    "-maxcpucount:1",
  ],
];

/** @param {string} root @param {string[][]} commands */
export function runCommands(root, commands) {
  admitDotnetHost();
  artifactDirectory(root, "artifacts/bin");
  for (const [command = "", ...args] of commands) {
    process.stdout.write(`> ${[command, ...args].join(" ")}\n`);
    const result = spawnSync(...commandLine(command, args), { cwd: root, stdio: "inherit" });
    if (result.status !== 0) {
      throw new Error("A required source setup command failed.");
    }
  }
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  runCommands(fileURLToPath(new URL("../..", import.meta.url)), [
    ...restoreCommands,
    ["npm", "--prefix", "eng", "ci"],
    ...buildCommands,
  ]);
}
