// A Gitless container/source export publishes directly through the same provenance owner.
// This does not claim Git history qualification or a workstation run context.
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { resolve } from "node:path";
import { gitEnvironment } from "../scan/process.mjs";
import { publishCommand } from "./main.mjs";
try {
  const root = resolve(import.meta.dirname, "../../..");
  const git = spawnSync("git", ["rev-parse", "--show-toplevel"], {
    cwd: root,
    env: gitEnvironment(),
    encoding: "utf8",
  });
  assert.equal(
    git.status,
    128,
    "Source-export publication requires a Gitless tree outside Git ancestry.",
  );
  publishCommand(process.argv.slice(2));
} catch (error) {
  process.stderr.write(
    `${error instanceof Error ? error.message : "Source-export publication failed."}\n`,
  );
  process.exitCode = 1;
}
