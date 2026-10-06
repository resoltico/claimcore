import assert from "node:assert/strict";
import { chmodSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { setTimeout } from "node:timers/promises";

/** @param {(args: string[], expected?: number) => string} docker
 * @param {string} container @param {number} port
 */
async function waitForIssuerConnection(docker, container, port) {
  const remote = port.toString(16).toUpperCase().padStart(4, "0");
  const inspect = `if grep -qE ':${remote}[[:space:]]+01' /proc/net/tcp /proc/net/tcp6; then printf blocked; else printf waiting; fi`;
  for (let attempt = 0; attempt < 30; attempt += 1) {
    if (docker(["exec", container, "bash", "-c", inspect]).trim() === "blocked") {
      return;
    }
    await setTimeout(100);
  }
  throw new Error("Web did not reach the paused issuer connection before its startup deadline.");
}

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
    const starting = compose(["ps", "--quiet", "web"]).trim();
    assert.equal(JSON.parse(docker(["inspect", starting]))[0].State.Running, true);
    const installation = JSON.parse(readFileSync(join(configuration, "installation.json"), "utf8"));
    const issuer = new URL(installation.issuer);
    await waitForIssuerConnection(docker, starting, Number(issuer.port || "443"));
    compose(["stop", "web"]);
    assert.equal(JSON.parse(docker(["inspect", starting]))[0].State.ExitCode, 0);
  } finally {
    docker(["unpause", identity]);
  }
}
