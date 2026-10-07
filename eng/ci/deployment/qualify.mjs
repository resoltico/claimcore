import assert from "node:assert/strict";
import { randomBytes } from "node:crypto";
import { chmodSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { resolve, join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { publicationContext } from "./publication-context.mjs";
import { runner } from "./commands.mjs";
import { probe } from "./http.mjs";
import { databaseChecks } from "./database.mjs";
import { contextChecks } from "./context.mjs";
import { browserTrustQualification } from "./browser-trust-qualification.mjs";
import { revocationQualification } from "./revocation-qualification.mjs";
import { evidenceIdentity, verifyDeploymentReport } from "./report.mjs";
import { lifecycleChecks } from "./lifecycle.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");
const identity = evidenceIdentity(root);
const run = `claimcore-operating-${randomBytes(8).toString("hex")}`;
const state = join(root, "artifacts", run);
const configuration = join(state, "configuration");
const uid = process.getuid?.();
const gid = process.getgid?.();
if (uid === undefined || gid === undefined || uid === 0) {
  throw new Error("Run container qualification as a non-root POSIX user.");
}
mkdirSync(configuration, { recursive: true, mode: 0o700 });
chmodSync(configuration, 0o700);
/** @type {NodeJS.ProcessEnv} */
const env = {
  ...process.env,
  CLAIMCORE_COMPOSE_PROJECT: run,
  CLAIMCORE_CONFIG_DIR: configuration,
  CLAIMCORE_SERVICE_UID: String(uid),
  CLAIMCORE_SERVICE_GID: String(gid),
  CLAIMCORE_HOST_PORT: "0",
  CLAIMCORE_IDENTITY_HOST_PORT: "0",
};
const log = join(state, "commands.log");
const { docker } = runner(root, log, env);
let { compose } = runner(root, log, env);

let passed = false;
let browserTrust;
let databaseRevocation;
let publications;
try {
  contextChecks(root, state, docker);
  env.CLAIMCORE_PUBLISHED_CONTEXT = publicationContext(docker, state, env.CLAIMCORE_PUBLISHED_DIR);
  ({ compose } = runner(root, log, env));
  compose(["build", "web", "administration", "configure", "revocation"]);
  compose([
    "run",
    "--rm",
    "--no-deps",
    "configure",
    "/configuration",
    "/metadata",
    String(uid),
    String(gid),
    "Etc/UTC",
  ]);
  compose(["up", "--detach", "--wait", "primary", "witness", "identity", "revocation"]);
  compose(["run", "--rm", "initialize"]);
  publications = Object.fromEntries(
    ["web", "database"].map((product) => {
      const manifest = JSON.parse(
        compose([
          "run",
          "--rm",
          "--no-deps",
          "--entrypoint",
          "cat",
          "administration",
          `/app/publication-manifests/${product}.json`,
        ]),
      );
      return [product, manifest.treeSha256];
    }),
  );
  compose(["run", "--rm", "--no-deps", "administration", "verify"]);
  compose(["up", "--detach", "--wait", "web"]);
  const container = compose(["ps", "--quiet", "web"]).trim();
  const port = Number(compose(["port", "web", "5443"]).trim().split(":").at(-1));
  const ca = readFileSync(join(configuration, "web", "ca.pem"));
  const live = await probe(port, ca, "/health/live");
  assert.equal(live.status, 200);
  assert.equal((await probe(port, ca, "/health/live", "foreign.example.test")).status, 403);
  await assert.rejects(
    probe(port, ca, "/health/live", "app.localhost:5443", "foreign.example.test"),
    { code: "ERR_TLS_CERT_ALTNAME_INVALID" },
  );
  const ready = await probe(port, ca, "/health/ready");
  assert.equal(ready.status, 503);
  assert.equal(ready.headers["x-claimcore-data-use-scope"], "SYNTHETIC_ONLY");
  /** @type {{Config: {User: string}, HostConfig: {ReadonlyRootfs: boolean}, Mounts: {Source: string, Destination: string}[]}[]} */
  const [inspection] = JSON.parse(docker(["inspect", container]));
  assert.ok(inspection);
  assert.notEqual(inspection.Config.User.split(":")[0], "0");
  assert.equal(inspection.HostConfig.ReadonlyRootfs, true);
  assert.ok(
    inspection.Mounts.every(
      (mount) =>
        !mount.Source.endsWith("/administration") && !mount.Destination.includes("docker.sock"),
    ),
  );
  compose([
    "run",
    "--rm",
    "--no-deps",
    "--entrypoint",
    "bash",
    "web",
    "-c",
    "test ! -e /app/ClaimCore.Database.dll && test ! -e /app/initialize-local.sh && test ! -e /etc/claimcore/runtime/../administration && test ! -e /etc/claimcore/runtime/../authority && test ! -e /etc/claimcore/runtime/../identity",
  ]);
  browserTrust = browserTrustQualification(docker, compose, configuration, run);
  databaseRevocation = revocationQualification(compose);
  const installation = databaseChecks(compose);
  const initialInstallation = installation();
  await lifecycleChecks(compose, docker, configuration);
  compose([
    "up",
    "--detach",
    "--wait",
    "--force-recreate",
    "primary",
    "witness",
    "identity",
    "revocation",
  ]);
  compose(["run", "--rm", "initialize"], 1);
  assert.equal(installation(), initialInstallation);
  compose(["up", "--detach", "--wait", "--force-recreate", "web"]);
  const replacementPort = Number(compose(["port", "web", "5443"]).trim().split(":").at(-1));
  assert.equal((await probe(replacementPort, ca, "/health/live")).status, 200);
  passed = true;
} finally {
  compose(["down"]);
  const auxiliary = docker([
    "ps",
    "--all",
    "--quiet",
    "--filter",
    `label=org.claimcore.test-run=${run}`,
  ])
    .trim()
    .split("\n")
    .filter(Boolean);
  for (const container of auxiliary) {
    const [owned] = JSON.parse(docker(["inspect", container]));
    assert.equal(owned.Config.Labels["org.claimcore.test-run"], run);
    docker(["rm", "--force", container]);
  }
  if (passed) {
    const volumes = docker([
      "volume",
      "ls",
      "--quiet",
      "--filter",
      `label=com.docker.compose.project=${run}`,
    ])
      .trim()
      .split("\n")
      .filter(Boolean);
    for (const volume of volumes) {
      const [inspection] = JSON.parse(docker(["volume", "inspect", volume]));
      assert.equal(inspection.Labels["com.docker.compose.project"], run);
      docker(["volume", "rm", volume]);
    }
    rmSync(configuration, { recursive: true, force: true });
    rmSync(join(state, "publication-context"), { recursive: true, force: true });
  }
}

assert.deepEqual(evidenceIdentity(root), identity, "Qualification source changed while it ran.");
const report = verifyDeploymentReport(
  {
    format: "claimcore-deployment-qualification",
    formatVersion: 2,
    identity,
    publications,
    run,
    result: "passed",
    browserTrust,
    databaseRevocation,
    properties: {
      liveness: true,
      syntheticReadinessRefused: true,
      exactHostRefusal: true,
      tlsNameRefusal: true,
      nonRoot: true,
      privateMountSeparation: true,
      stopAndReplacement: true,
      startupStop: true,
      privateInputRefusals: true,
      runtimeAdministrationRefused: true,
      installationPreserved: true,
      privateBuildInputsExcluded: true,
    },
  },
  identity,
);
const reportPath = env.CLAIMCORE_DEPLOYMENT_REPORT ?? join(state, "result.json");
mkdirSync(dirname(reportPath), { recursive: true, mode: 0o700 });
writeFileSync(reportPath, `${JSON.stringify(report)}\n`, { mode: 0o600, flag: "wx" });
process.stdout.write(
  "Container operation qualification passed against the real HTTPS deployment.\n",
);
