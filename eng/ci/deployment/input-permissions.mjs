import assert from "node:assert/strict";

/** @param {(args: string[], expected?: number) => string} compose
 * @param {(args: string[], expected?: number) => string} docker
 * @param {string} container
 */
export function inputPermissionChecks(compose, docker, container) {
  const [web] = JSON.parse(docker(["inspect", container]));
  const volume = web.Mounts.find(
    /** @param {{Destination: string}} mount */ (mount) =>
      mount.Destination === "/etc/claimcore/runtime",
  );
  assert.equal(volume.Type, "volume");
  const [input] = JSON.parse(docker(["volume", "inspect", volume.Name]));
  assert.equal(
    input.Labels["com.docker.compose.project"],
    web.Config.Labels["com.docker.compose.project"],
  );
  /** @param {number} mode */
  const chmod = (mode) =>
    docker([
      "run",
      "--rm",
      "--user",
      "0:0",
      "--mount",
      `type=volume,source=${volume.Name},target=/configuration`,
      "--entrypoint",
      "node",
      "claimcore-configuration:source",
      "-e",
      `require('fs').chmodSync('/configuration/installation/web/primary.connection',${mode})`,
    ]);
  chmod(0o644);
  try {
    compose(["run", "--rm", "--no-deps", "web"], 3);
  } finally {
    chmod(0o600);
  }
  compose(["run", "--rm", "--no-deps", "--user", "65001:65001", "web"], 3);
}
