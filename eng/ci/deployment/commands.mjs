import { spawnSync } from "node:child_process";
import { appendFileSync } from "node:fs";

/** @param {string} root @param {string} log @param {NodeJS.ProcessEnv} env */
export function runner(root, log, env) {
  /** @param {string[]} args @param {number} [expected] @param {string} [input] @returns {string} */
  const docker = (args, expected = 0, input = "") => {
    const result = spawnSync("docker", args, {
      input,
      cwd: root,
      env,
      encoding: "utf8",
      timeout: 30 * 60 * 1000,
      maxBuffer: 64 * 1024 * 1024,
    });
    appendFileSync(log, result.stdout ?? "", { mode: 0o600 });
    appendFileSync(log, result.stderr ?? "");
    if (result.status !== expected) {
      throw new Error("Container qualification command failed; private run diagnostics retained.");
    }
    return result.stdout;
  };
  const base = [
    "compose",
    "--file",
    "deployment/compose.yaml",
    "--file",
    "deployment/administration.compose.yaml",
    "--file",
    "deployment/local.compose.yaml",
  ];
  if (env.CLAIMCORE_PUBLISHED_CONTEXT !== undefined) {
    base.push("--file", "deployment/qualification.compose.yaml");
  }
  return {
    docker,
    compose: /** @param {string[]} args @param {number} [expected] */ (args, expected) =>
      docker([...base, ...args], expected),
  };
}
