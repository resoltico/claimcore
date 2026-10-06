import assert from "node:assert/strict";
import { chmodSync } from "node:fs";
import { join } from "node:path";
import { setTimeout } from "node:timers/promises";

/** @param {(args: string[], expected?: number) => string} compose
 * @param {(args: string[], expected?: number) => string} docker
 * @param {string} configuration
 */
export async function lifecycleChecks(compose, docker, configuration) {
  const container = compose(["ps", "--quiet", "web"]).trim();
  compose(["stop", "web"]);
  assert.equal(JSON.parse(docker(["inspect", container]))[0].State.ExitCode, 0);
  const privateInput = join(configuration, "web", "primary.connection");
  chmodSync(privateInput, 0o644);
  try {
    compose(["run", "--rm", "--no-deps", "web"], 3);
  } finally {
    chmodSync(privateInput, 0o600);
  }
  compose(["run", "--rm", "--no-deps", "--user", "65001:65001", "web"], 3);
  const identity = compose(["ps", "--quiet", "identity"]).trim();
  docker(["pause", identity]);
  try {
    compose(["up", "--detach", "--no-deps", "--force-recreate", "web"]);
    await setTimeout(1000);
    const starting = compose(["ps", "--quiet", "web"]).trim();
    assert.equal(JSON.parse(docker(["inspect", starting]))[0].State.Running, true);
    compose(["stop", "web"]);
    assert.equal(JSON.parse(docker(["inspect", starting]))[0].State.ExitCode, 0);
  } finally {
    docker(["unpause", identity]);
  }
}
