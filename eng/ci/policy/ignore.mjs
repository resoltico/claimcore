import { executable } from "../executable.mjs";
// The git ignore policy: private and generated paths must be ignored, public release inputs must
// exist and must not be. Git classifies the probes against the working tree's .gitignore files
// with an isolated, empty index, so nothing tracked or staged can change the answer.
import { existsSync, mkdtempSync, readFileSync, rmSync, statSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { gitEnvironment } from "../scan/process.mjs";
import { spawnSync } from "node:child_process";

/**
 * @typedef {object} IgnorePolicy
 * @property {string[]} mustBeIgnored
 * @property {string[]} mustRemainVisible
 */

/**
 * @param {string} root
 * @returns {IgnorePolicy}
 */
export function loadIgnorePolicy(root) {
  const policy = JSON.parse(readFileSync(join(root, "config/git-ignore-policy.json"), "utf8"));
  if (policy.schemaVersion !== 1) {
    throw new Error("config/git-ignore-policy.json must be schema 1.");
  }
  return policy;
}

/**
 * Ask git which of `probes` are ignored under `root`.
 * @param {string} root
 * @param {string[]} probes
 * @returns {Set<string>}
 */
function ignoredAmong(root, probes) {
  const scratch = mkdtempSync(join(tmpdir(), "claimcore-ignore-policy-"));
  try {
    const database = join(scratch, "inventory.git");
    const env = gitEnvironment();
    const init = spawnSync(executable("git"), ["init", "--bare", "--quiet", database], { env });
    if (init.status !== 0) {
      throw new Error("The isolated Git ignore database could not be initialized.");
    }
    const check = spawnSync(
      executable("git"),
      [
        "--git-dir",
        database,
        "--work-tree",
        root,
        "-c",
        "core.quotePath=false",
        "check-ignore",
        "--no-index",
        "--stdin",
      ],
      { env, input: probes.join("\n"), encoding: "utf8" },
    );
    if (check.status !== 0 && check.status !== 1) {
      throw new Error("Git could not classify the ignore-policy probes.");
    }
    return new Set(check.stdout.split("\n").filter((line) => line !== ""));
  } finally {
    rmSync(scratch, { recursive: true, force: true });
  }
}

/**
 * @param {string} root
 * @param {IgnorePolicy} [policy]
 * @returns {string} The verdict line.
 */
export function checkIgnorePolicy(root, policy = loadIgnorePolicy(root)) {
  const repository = resolve(root);
  if (!existsSync(repository) || !statSync(repository).isDirectory()) {
    throw new Error("The repository root does not exist.");
  }
  const ignored = ignoredAmong(repository, [...policy.mustBeIgnored, ...policy.mustRemainVisible]);
  for (const path of policy.mustBeIgnored) {
    if (!ignored.has(path)) {
      throw new Error(`A required private or generated path is not ignored: ${path}`);
    }
  }
  for (const path of policy.mustRemainVisible) {
    if (!existsSync(join(repository, path))) {
      throw new Error(`A required public release input is missing: ${path}`);
    }
    if (ignored.has(path)) {
      throw new Error(`A required public release input is ignored: ${path}`);
    }
  }
  return `Git ignore policy passed for ${policy.mustBeIgnored.length} private/generated probes and ${policy.mustRemainVisible.length} public release inputs.`;
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  try {
    process.stdout.write(
      `${checkIgnorePolicy(resolve(dirname(fileURLToPath(import.meta.url)), "../../.."))}\n`,
    );
  } catch (error) {
    process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
    process.exitCode = 1;
  }
}
