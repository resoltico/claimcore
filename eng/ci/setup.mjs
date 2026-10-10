import { admitDotnetHost } from "./dotnet-host.mjs";
// Prepare a fresh checkout: restore the locked dependency graphs, the pinned local tools and the
// pinned downloadable tools, generate the contract artifacts, then build.
//
//   node eng/ci/setup.mjs
import { buildCommands, restoreCommands, runCommands } from "./source-build.mjs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

admitDotnetHost();
const root = resolve(dirname(fileURLToPath(import.meta.url)), "../..");

/** @type {string[][]} */
const steps = [
  ...restoreCommands,
  ["npm", "--prefix", "eng", "ci"],
  ["npm", "--prefix", "web", "ci"],
  ["node", "eng/ci/tools.mjs"],
  ["npm", "--prefix", "web", "run", "contract:generate"],
  ...buildCommands,
];

runCommands(root, steps);
