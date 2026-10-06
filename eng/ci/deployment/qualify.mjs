import assert from "node:assert/strict";
import { randomBytes } from "node:crypto";
import { chmodSync, cpSync, mkdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { resolve, join, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { verifyPublished } from "../publish/main.mjs";
import { runner } from "./commands.mjs";
import { probe } from "./http.mjs";
import { databaseChecks } from "./database.mjs";
import { contextChecks } from "./context.mjs";
import { lifecycleChecks } from "./lifecycle.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");
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
const publication = process.env.CLAIMCORE_PUBLISHED_DIR;
if (publication !== undefined) {
  verifyPublished(publication);
  const context = join(state, "publication-context");
  mkdirSync(context);
  cpSync(publication, join(context, "published"), { recursive: true });
  env.CLAIMCORE_PUBLISHED_CONTEXT = context;
}
const { docker, compose } = runner(root, join(state, "commands.log"), env);

let passed = false;
try {
  contextChecks(root, state, docker);
  compose(["build", "web", "administration", "configure", "revocation"]);
  compose([
    "run",
    "--rm",
    "--no-deps",
    "configure",
    "/configuration",
    String(uid),
    String(gid),
    "Etc/UTC",
  ]);
  compose(["up", "--detach", "--wait", "primary", "witness", "identity"]);
  compose(["run", "--rm", "--no-deps", "administration", "verify"], 3);
  compose(["up", "--detach", "--wait", "revocation"]);
  compose(["run", "--rm", "initialize"]);
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
    "test ! -e /app/ClaimCore.Database.dll && test ! -e /app/initialize-local.sh",
  ]);
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
    docker([
      "run",
      "--rm",
      "--user",
      "0:0",
      "--mount",
      `type=bind,source=${configuration},target=/configuration`,
      "--entrypoint",
      "node",
      "claimcore-configuration:source",
      "-e",
      "const fs=require('fs'); const state=JSON.parse(fs.readFileSync('/configuration/installation.json')); if(state.format!=='claimcore-local-configuration-1'||state.scope!=='SYNTHETIC_ONLY')throw Error('Foreign configuration refused'); for(const name of fs.readdirSync('/configuration'))fs.rmSync('/configuration/'+name,{recursive:true,force:true});",
    ]);
    rmSync(configuration, { recursive: true, force: true });
    rmSync(join(state, "publication-context"), { recursive: true, force: true });
  }
}

writeFileSync(
  join(state, "result.json"),
  `${JSON.stringify({
    run,
    result: "passed",
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
    missingRevocationRefused: true,
    installationPreserved: true,
    privateBuildInputsExcluded: true,
  })}\n`,
  { mode: 0o600, flag: "wx" },
);
process.stdout.write(
  "Container operation qualification passed against the real HTTPS deployment.\n",
);
