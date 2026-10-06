import assert from "node:assert/strict";

/** @typedef {(args: string[], expected?: number, input?: string) => string} Docker */
/** @param {Docker} docker @param {string} container @param {string} run */
function inspectExpiry(docker, container, run) {
  const [server] = JSON.parse(docker(["inspect", container]));
  assert.equal(server.Config.Labels["org.claimcore.test-run"], run);
  assert.equal(server.Mounts.length, 1);
  assert.equal(server.Mounts[0].Destination, "/certificate");
  assert.equal(server.Mounts[0].RW, false);
  assert.equal(server.HostConfig.Mounts[0].VolumeOptions.Subpath, "installation/browser-expiry");
  return server;
}

/** Owner-side issuance and a separate leaf-only server; no CA key enters the browser. @param {Docker} docker @param {string} volume @param {string} network @param {string} run */
export function expiryServer(docker, volume, network, run) {
  const [configuration] = JSON.parse(docker(["volume", "inspect", volume]));
  assert.equal(configuration.Labels["com.docker.compose.project"], run);
  docker([
    "run",
    "--rm",
    "--user",
    "0:0",
    "--label",
    `org.claimcore.test-run=${run}`,
    "--mount",
    `type=volume,source=${volume},target=/configuration`,
    "--entrypoint",
    "node",
    "claimcore-configuration:source",
    "/source/eng/operations/browser-expiry-fixture.mjs",
    "issue",
  ]);
  const container = docker([
    "run",
    "--detach",
    "--user",
    "1000:0",
    "--label",
    `org.claimcore.test-run=${run}`,
    "--network",
    network,
    "--read-only",
    "--cap-drop",
    "ALL",
    "--security-opt",
    "no-new-privileges:true",
    "--mount",
    `type=volume,source=${volume},target=/certificate,readonly,volume-subpath=installation/browser-expiry`,
    "--entrypoint",
    "node",
    "claimcore-configuration:source",
    "/source/eng/operations/browser-expiry-fixture.mjs",
    "serve",
  ]).trim();
  const server = inspectExpiry(docker, container, run);
  return {
    address: server.NetworkSettings.Networks[network].IPAddress,
    close: () => {
      const [current] = JSON.parse(docker(["inspect", container]));
      assert.equal(current.Config.Labels["org.claimcore.test-run"], run);
      docker(["rm", "--force", container]);
    },
  };
}
