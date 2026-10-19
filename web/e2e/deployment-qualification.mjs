import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { existsSync, readFileSync, writeFileSync } from "node:fs";
import https from "node:https";
import net from "node:net";
import path from "node:path";
import process from "node:process";
import { chromium } from "@playwright/test";

const [root, owned] = process.argv.slice(2);
const configuration = path.join(owned, "configuration $CLAIMCORE_QA_PATH # :, \" \\\' end\\");
const composeFile = path.join(root, "deployment/compose.yaml");
const cleanupImage = JSON.parse(
  readFileSync(path.join(root, "db/postgresql-baseline.json"), "utf8"),
).supportedMajors[0].containerImage;
let project;
let stage = "configure";
let sequence = 0;
const progress = (value) => {
  stage = value;
  writeFileSync(path.join(owned, "deployment.progress"), `${stage}\n`, { mode: 0o600 });
};
const command = (executable, args, timeout = 1_200_000, expectedExit = 0) => {
  // Compose interpolation must never select an operator's existing configuration or project.
  const environment = Object.fromEntries(
    Object.entries(process.env).filter(
      ([name]) => !name.startsWith("CLAIMCORE_") && !name.startsWith("COMPOSE_"),
    ),
  );
  const result = spawnSync(executable, args, {
    cwd: root,
    env: environment,
    timeout,
    maxBuffer: 8_388_608,
    encoding: "utf8",
    stdio: "pipe",
  });
  const prefix = process.argv[4] === "cleanup" ? "cleanup-command" : "command";
  const output = [result.stdout ?? "", result.stderr ?? ""].join("\n");
  writeFileSync(path.join(owned, `${prefix}-${sequence++}.log`), output, { mode: 0o600 });
  if (result.error !== undefined || result.status !== expectedExit) {
    throw new Error("Owned deployment command refused.", { cause: result.error });
  }
  return result.stdout;
};
const compose = (...args) => {
  if (!/^claimcore-[a-f0-9]{16}$/u.test(project ?? "")) {
    throw new Error("Invalid owned project identity.");
  }
  return command("docker", [
    "compose",
    "--project-name",
    project,
    "--env-file",
    path.join(configuration, "compose.env"),
    "--file",
    composeFile,
    ...args,
  ]);
};
const rejectUnstartedIdentity = () => {
  const configurationValue = JSON.parse(compose("config", "--format", "json"));
  const { identity } = configurationValue.services;
  const probe = identity.healthcheck.test;
  if (probe[0] !== "CMD") {
    throw new Error("The owned identity readiness probe must be an explicit command.");
  }
  command(
    "docker",
    [
      "run",
      "--rm",
      "--name",
      `${project}-readiness-negative`,
      "--label",
      `claimcore.configuration=${project}`,
      "--network",
      "none",
      "--entrypoint",
      probe[1],
      identity.image,
      ...probe.slice(2),
    ],
    120_000,
    1,
  );
};
const qualifyIdentityMemory = () => {
  const identity = compose("ps", "--quiet", "identity").trim();
  if (!/^[a-f0-9]{64}$/u.test(identity)) {
    throw new Error("The owned identity container was not uniquely selected.");
  }
  const memory = command(
    "docker",
    ["inspect", "--format", "{{.HostConfig.Memory}}", identity],
    60_000,
  ).trim();
  if (memory !== "2147483648") {
    throw new Error("The owned identity container does not retain its memory bound.");
  }
};
const port = () =>
  new Promise((resolve, reject) => {
    const server = net.createServer();
    server.on("error", reject);
    server.listen(0, "127.0.0.1", () => {
      const selected = server.address().port;
      server.close((error) => (error ? reject(error) : resolve(selected)));
    });
  });
const settings = () =>
  Object.fromEntries(
    readFileSync(path.join(configuration, "compose.env"), "utf8")
      .trim()
      .split("\n")
      .map((line) => {
        const position = line.indexOf("=");
        return [line.slice(0, position), line.slice(position + 1)];
      }),
  );
const ready = (address, ca) =>
  new Promise((resolve) => {
    const request = https.get(
      address,
      {
        ca,
        timeout: 2_000,
        family: 4,
        lookup: (_hostname, _options, callback) => callback(null, "127.0.0.1", 4),
      },
      (response) => {
        response.resume();
        response.once("end", () => resolve(response.statusCode === 200));
      },
    );
    request.once("error", () => resolve(false));
    request.once("timeout", () => request.destroy());
  });
const awaitReady = async (origin, ca, route = "/health/ready") => {
  const deadline = Date.now() + 240_000;
  while (Date.now() < deadline) {
    if (await ready(`${origin}${route}`, ca)) {
      return;
    }
    await new Promise((resolve) => {
      setTimeout(resolve, 300);
    });
  }
  throw new Error("Owned deployment readiness deadline.");
};
const payload = (response) => {
  if (response.status() !== 200) {
    throw new Error("Unexpected owned service status.");
  }
  return response.json();
};
const session = async (context, origin, fingerprint) => {
  const value = await payload(
    await context.request.get(`${origin}/api/session`, {
      headers: { "ClaimCore-Contract": fingerprint },
    }),
  );
  if (
    value.outcome.tag !== "SUCCEEDED" ||
    !value.outcome.data.authenticated ||
    !value.outcome.data.antiforgeryToken
  ) {
    throw new Error("Owned session was not authenticated.");
  }
  return value.outcome.data.antiforgeryToken;
};
const post = (context, origin, endpoint, fingerprint, csrf) =>
  context.request.post(`${origin}/api/${endpoint}`, {
    headers: { "ClaimCore-Contract": fingerprint, "X-ClaimCore-Antiforgery": csrf, Origin: origin },
    data: {},
  });
const owner = async (context, origin, fingerprint, csrf) => {
  const value = await payload(await post(context, origin, "context", fingerprint, csrf));
  if (
    value.outcome.tag !== "SUCCEEDED" ||
    !value.outcome.data.capabilities.includes("MANAGE_AUTHORITY")
  ) {
    throw new Error("Fresh owner authority was not provisioned.");
  }
};
const login = async (context, origin) => {
  const page = await context.newPage();
  await page.goto(`${origin}/auth/login`);
  await page.locator("#username").fill("owner");
  const password = readFileSync(path.join(configuration, "owner-login.password"));
  try {
    await page.locator("#password").fill(password.toString("utf8").trim());
    await page.locator("#kc-login").click();
    await page.waitForURL((url) => url.origin === origin && url.pathname === "/");
  } finally {
    password.fill(0);
    await page.close();
  }
};
const qualify = async (origin, ca) => {
  const fingerprint = createHash("sha256")
    .update(readFileSync(path.join(root, "contracts/openapi.json")))
    .digest("hex");
  const browser = await chromium.launch({ headless: true });
  try {
    // Only this synthetic browser ignores fixture certificates. Readiness above verifies its explicit CA and hostname.
    const context = await browser.newContext({ ignoreHTTPSErrors: true });
    await login(context, origin);
    const csrf = await session(context, origin, fingerprint);
    await owner(context, origin, fingerprint, csrf);
    progress("restart");
    compose("restart", "web");
    await awaitReady(origin, ca);
    await owner(context, origin, fingerprint, csrf);
    await session(context, origin, fingerprint);
    progress("logout");
    await payload(await post(context, origin, "session.logout", fingerprint, csrf));
    const ended = await payload(
      await context.request.get(`${origin}/api/session`, {
        headers: { "ClaimCore-Contract": fingerprint },
      }),
    );
    if (ended.outcome.data.authenticated) {
      throw new Error("Owned logout retained its session.");
    }
    await context.close();
  } finally {
    await browser.close();
  }
};
const cleanup = () => {
  if (project === undefined && existsSync(path.join(configuration, "compose.env"))) {
    project = settings().CLAIMCORE_COMPOSE_PROJECT;
  }
  if (project === undefined) {
    return;
  }
  compose("down", "--volumes", "--remove-orphans", "--rmi", "local", "--timeout", "20");
  const values = settings();
  if (!/^[0-9]+$/u.test(values.CLAIMCORE_UID) || !/^[0-9]+$/u.test(values.CLAIMCORE_GID)) {
    throw new Error("Invalid generated cleanup identity.");
  }
  command(
    "docker",
    [
      "run",
      "--rm",
      "--name",
      `${project}-cleanup`,
      "--label",
      `claimcore.configuration=${project}`,
      "--network",
      "none",
      "--read-only",
      "--mount",
      `type=bind,"source=${configuration.replaceAll('"', '""')}",target=/config`,
      "--user",
      "0",
      "--entrypoint",
      "/bin/sh",
      cleanupImage,
      "-c",
      `chown -R ${values.CLAIMCORE_UID}:${values.CLAIMCORE_GID} /config`,
    ],
    60_000,
  );
};
let failed = false;
if (process.argv[4] === "cleanup") {
  try {
    cleanup();
  } catch {
    failed = true;
  }
  process.exit(failed ? 1 : 0);
}
try {
  progress("configure");
  const webPort = await port();
  let identityPort = await port();
  while (identityPort === webPort) {
    identityPort = await port();
  }
  command(
    "bash",
    [
      path.join(root, "deployment/configure-local.sh"),
      configuration,
      "Etc/UTC",
      String(webPort),
      String(identityPort),
    ],
    120_000,
  );
  project = settings().CLAIMCORE_COMPOSE_PROJECT;
  progress("readiness-negative");
  rejectUnstartedIdentity();
  progress("build-and-start");
  compose("up", "--build", "--detach", "--wait", "--wait-timeout", "240");
  qualifyIdentityMemory();
  const ca = readFileSync(path.join(configuration, "ca.pem"));
  const origin = `https://localhost:${webPort}`;
  await awaitReady(origin, ca);
  await awaitReady(
    `https://identity.localhost:${identityPort}`,
    ca,
    "/realms/claimcore/.well-known/openid-configuration",
  );
  progress("owner-login");
  await qualify(origin, ca);
  progress("qualified");
} catch {
  failed = true;
  process.stderr.write(`Fresh deployment qualification failed during ${stage}.\n`);
  if (project !== undefined) {
    try {
      compose("logs", "--no-color", "--timestamps");
      const container = compose("ps", "--all", "--quiet", "web").trim();
      if (/^[a-f0-9]{64}$/u.test(container)) {
        command("docker", ["inspect", "--format", "{{json .State}}", container], 60_000);
      }
    } catch {
      process.stderr.write("Owned deployment logs could not be retained.\n");
    }
  }
} finally {
  try {
    cleanup();
  } catch {
    failed = true;
    process.stderr.write("Fresh deployment cleanup failed for the owned project.\n");
  }
}
process.exitCode = failed ? 1 : 0;
