import { expiryServer } from "./browser-trust-expiry.mjs";
import assert from "node:assert/strict";
import { X509Certificate } from "node:crypto";
import { readFileSync } from "node:fs";
import { join } from "node:path";

/** @param {unknown} value @param {string} fingerprint */
export function verifyBrowserTrustReport(value, fingerprint) {
  assert.ok(value !== null && typeof value === "object");
  assert.deepEqual(Object.keys(value).sort(), [
    "expiredCertificateRefused",
    "publicCaSha256",
    "removedTrustRefused",
    "scope",
    "trustedAuthentication",
    "untrustedRefused",
    "version",
    "wrongHostnameRefused",
  ]);
  assert.ok("scope" in value && value.scope === "CHROMIUM_LINUX_NSS");
  assert.ok(
    "version" in value &&
      typeof value.version === "string" &&
      /^[0-9]+(?:\.[0-9]+){3}$/u.test(value.version),
  );
  assert.ok("publicCaSha256" in value && value.publicCaSha256 === fingerprint);
  for (const flag of [
    "expiredCertificateRefused",
    "untrustedRefused",
    "trustedAuthentication",
    "wrongHostnameRefused",
    "removedTrustRefused",
  ]) {
    assert.ok(flag in value && Reflect.get(value, flag) === true);
  }
  return value;
}

/** @typedef {(args: string[], expected?: number, input?: string) => string} Docker */
/** @typedef {(args: string[], expected?: number) => string} Compose */
/** @param {Docker} docker @param {Compose} compose @param {string} run */
function networkAddresses(docker, compose, run) {
  /** @type {Record<string, string>} */
  const addresses = {};
  const network = `${run}_default`;
  let volume;
  for (const service of ["web", "identity"]) {
    const container = compose(["ps", "--quiet", service]).trim();
    const [info] = JSON.parse(docker(["inspect", container]));
    assert.equal(info.Config.Labels["com.docker.compose.project"], run);
    assert.deepEqual(Object.keys(info.NetworkSettings.Networks), [network]);
    if (service === "web") {
      volume = info.Mounts.find(
        (/** @type {{Destination: string}} */ mount) =>
          mount.Destination === "/etc/claimcore/runtime",
      ).Name;
    }
    addresses[service] = info.NetworkSettings.Networks[network].IPAddress;
  }
  return { network, addresses, volume };
}

const browserPolicy = [
  "--read-only",
  "--cap-drop",
  "ALL",
  "--security-opt",
  "no-new-privileges:true",
  "--tmpfs",
  "/home/node:rw,uid=1000,gid=1000,mode=700",
  "--tmpfs",
  "/tmp:rw,mode=1777",
];

/** @param {Compose} compose */
const initialOwner = (compose) =>
  JSON.parse(
    compose([
      "run",
      "--rm",
      "--no-deps",
      "--entrypoint",
      "cat",
      "configure",
      "/configuration/installation/administration/initial-owner.json",
    ]),
  );

/** @param {Docker} docker @param {Compose} compose @param {string} configuration @param {string} run */
export function browserTrustQualification(docker, compose, configuration, run) {
  const { network, addresses, volume } = networkAddresses(docker, compose, run);
  const image = `${run}-browser:source`;
  const ca = readFileSync(join(configuration, "web", "ca.pem"), "utf8");
  docker([
    "build",
    "--file",
    "deployment/Dockerfile",
    "--target",
    "browser-qualification",
    "--tag",
    image,
    ".",
  ]);
  const expiry = expiryServer(docker, volume, network, run);
  const input = JSON.stringify({
    owner: initialOwner(compose),
    expiry: expiry.address,
    ca,
    password: readFileSync(join(configuration, "owner.password"), "utf8").trim(),
    web: addresses.web,
    identity: addresses.identity,
  });
  try {
    const result = docker(
      [
        "run",
        "--rm",
        "--interactive",
        "--label",
        `org.claimcore.test-run=${run}`,
        "--network",
        network,
        ...browserPolicy,
        image,
      ],
      0,
      input,
    );
    return verifyBrowserTrustReport(JSON.parse(result), new X509Certificate(ca).fingerprint256);
  } finally {
    expiry.close();
    docker(["image", "rm", image]);
  }
}
