// Real CLI, TLS and HTTP dispatch; the responder holds a synthetic mutation response.
import assert from "node:assert/strict";
import { spawn, spawnSync } from "node:child_process";
import { randomUUID } from "node:crypto";
import { chmodSync, mkdirSync, readFileSync, realpathSync, rmSync, writeFileSync } from "node:fs";
import { createServer } from "node:https";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { fileURLToPath } from "node:url";

/** @template T @param {Promise<T>} work @param {number} milliseconds */
async function bounded(work, milliseconds) {
  /** @type {ReturnType<typeof setTimeout> | undefined} */
  let timer;
  const expired = new Promise((_, reject) => {
    timer = setTimeout(
      () => reject(new Error("Synthetic interruption fixture timed out.")),
      milliseconds,
    );
  });
  try {
    return await Promise.race([work, expired]);
  } finally {
    clearTimeout(timer);
  }
}

/** @param {string[]} args */
function generateCertificate(args) {
  const result = spawnSync("openssl", args, { stdio: "ignore", timeout: 10_000 });
  assert.equal(result.status, 0, "Synthetic certificate generation failed.");
}

/** @param {string} directory */
function certificateAuthority(directory) {
  const key = join(directory, "ca-key.pem");
  const certificate = join(directory, "ca-certificate.pem");
  generateCertificate([
    "req",
    "-x509",
    "-newkey",
    "rsa:2048",
    "-nodes",
    "-days",
    "1",
    "-subj",
    "/CN=synthetic-interruption-ca",
    "-addext",
    "basicConstraints=critical,CA:TRUE",
    "-addext",
    "keyUsage=critical,keyCertSign,cRLSign",
    "-keyout",
    key,
    "-out",
    certificate,
  ]);
  chmodSync(key, 0o600);
  chmodSync(certificate, 0o600);
  return { key, certificate };
}

/** @param {string} directory @param {{key: string, certificate: string}} ca */
function serverCertificate(directory, ca) {
  const key = join(directory, "server-key.pem");
  const certificate = join(directory, "server-certificate.pem");
  const request = join(directory, "server.csr");
  const extensions = join(directory, "server.ext");
  writeFileSync(
    extensions,
    "basicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature,keyEncipherment\nextendedKeyUsage=serverAuth\nsubjectAltName=IP:127.0.0.1\n",
    { mode: 0o600 },
  );
  generateCertificate([
    "req",
    "-new",
    "-newkey",
    "rsa:2048",
    "-nodes",
    "-subj",
    "/CN=synthetic-interruption-server",
    "-keyout",
    key,
    "-out",
    request,
  ]);
  generateCertificate([
    "x509",
    "-req",
    "-in",
    request,
    "-CA",
    ca.certificate,
    "-CAkey",
    ca.key,
    "-CAcreateserial",
    "-days",
    "1",
    "-extfile",
    extensions,
    "-out",
    certificate,
  ]);
  chmodSync(key, 0o600);
  chmodSync(certificate, 0o600);
  return { key, certificate };
}

/** @param {string} directory */
function credentials(directory) {
  const ca = certificateAuthority(directory);
  const server = serverCertificate(directory, ca);
  const secret = join(directory, "secret");
  writeFileSync(secret, "synthetic-only-secret", { mode: 0o600 });
  return {
    key: readFileSync(server.key),
    certificate: readFileSync(server.certificate),
    certificatePath: ca.certificate,
    secret,
  };
}

const [, , binary] = process.argv;
assert.ok(binary, "A built CLI path is required.");
const root = fileURLToPath(new URL("../../..", import.meta.url));
const directory = join(realpathSync(tmpdir()), `cli-interruption-${randomUUID()}`);
mkdirSync(directory, { mode: 0o700 });
/** @type {ReturnType<typeof credentials> | undefined} */
let material;
/** @type {ReturnType<typeof createServer> | undefined} */
let server;
let origin = "";
let metadataRequests = 0;
let tokenRequests = 0;
let tlsFailed = false;
/** @type {() => void} */
let dispatched = () => {
  throw new Error("Responder was not initialized.");
};
/** @type {(error: Error) => void} */
let refused = () => {
  throw new Error("Responder was not initialized.");
};
const observed = new Promise((resolve, reject) => {
  refused = reject;
  dispatched = () => resolve(true);
});
/** @type {import("node:child_process").ChildProcessWithoutNullStreams | undefined} */
let child;
try {
  material = credentials(directory);
  server = createServer({ key: material.key, cert: material.certificate }, (request, response) => {
    if (request.url?.endsWith("/.well-known/openid-configuration")) {
      metadataRequests += 1;
      response.writeHead(200, { "content-type": "application/json" });
      response.end(
        JSON.stringify({
          issuer: origin,
          authorization_endpoint: `${origin}authorize`,
          token_endpoint: `${origin}token`,
          jwks_uri: `${origin}keys`,
        }),
      );
    } else if (request.url === "/token") {
      tokenRequests += 1;
      request.resume();
      response.writeHead(200, { "content-type": "application/json" });
      response.end(
        JSON.stringify({ access_token: "synthetic-token", token_type: "Bearer", expires_in: 300 }),
      );
    } else {
      request.resume();
      dispatched();
    }
  });

  server.on("tlsClientError", () => {
    tlsFailed = true;
  });
  await new Promise((resolve, reject) => {
    server?.once("error", reject);
    server?.listen(0, "127.0.0.1", () => resolve(true));
  });
  const address = server.address();
  assert.ok(address && typeof address !== "string");
  origin = `https://127.0.0.1:${address.port}/`;
  const environment = { ...process.env };
  for (const key of Object.keys(environment)) {
    if (key.startsWith("COVERLET_")) {
      delete environment[key];
    }
  }
  Object.assign(environment, {
    CLAIMCORE_SERVICE_URL: origin,
    CLAIMCORE_OIDC_ISSUER: origin,
    CLAIMCORE_OIDC_CLIENT_ID: "synthetic-client",
    CLAIMCORE_CLI_AUTH_MODE: "automation",
    CLAIMCORE_OIDC_CLIENT_SECRET_FILE: material.secret,
    CLAIMCORE_CLI_OIDC_TRUST_ROOT_FILE: material.certificatePath,
    CLAIMCORE_CLI_SERVICE_TRUST_ROOT_FILE: material.certificatePath,
  });
  child = spawn("dotnet", [binary, "session"], {
    cwd: root,
    env: environment,
    stdio: ["pipe", "pipe", "pipe"],
  });
  let outputBytes = 0;
  let output = "";
  child.stdout.on("data", (bytes) => {
    outputBytes += bytes.length;
    output += bytes.toString();
    if (output.includes("\n")) {
      const response = JSON.parse(output.split("\n")[0] ?? "{}");
      refused(
        new Error(
          `CLI refused synthetic dispatch: ${metadataRequests}/${tokenRequests}/${tlsFailed}/${response.kind}/${response.code ?? response.reason ?? response.problem?.code ?? response.diagnostic?.id ?? "unknown"}`,
        ),
      );
    }
  });
  child.stderr.resume();
  const exit = new Promise((resolve, reject) => {
    child?.once("error", reject);
    child?.once("exit", (code) => resolve(code));
  });
  child.stdin.write(
    `${JSON.stringify({
      protocolVersion: 4,
      endpoint: "command.prepare",
      input: {
        operationId: "74000000-0000-4000-8000-000000000001",
        caseReference: "SYNTHETIC",
        expectedRevision: "0",
        command: { kind: "CLOSE", values: {} },
      },
    })}\n`,
  );
  await bounded(
    Promise.race([
      observed,
      exit.then(() => {
        throw new Error("CLI exited before synthetic mutation dispatch.");
      }),
    ]),
    10_000,
  );
  assert.ok(child.kill("SIGINT"), "Owned CLI signal could not be sent.");
  assert.equal(
    await bounded(exit, 3000),
    4,
    "Possible mutation interruption must remain uncertain.",
  );
  assert.equal(outputBytes, 0, "Held response cannot be fabricated after interruption.");
} finally {
  if (child && child.exitCode === null && child.signalCode === null) {
    const stopped = new Promise((resolve) => {
      child?.once("exit", () => resolve(true));
    });
    child.kill("SIGKILL");
    await stopped;
  }
  if (server) {
    server.closeAllConnections();
    await new Promise((resolve) => {
      server?.close(() => resolve(true));
    });
  }
  material?.key.fill(0);
  rmSync(directory, { recursive: true, force: true });
}
