import assert from "node:assert/strict";
import { execFileSync, spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { createAuthority, serverCertificate } from "./local-certificates.mjs";

/** @param {string} cert @param {string} ca @param {string} hostname @param {"browser" | "postgres"} recipient */
const assertLeaf = (cert, ca, hostname, recipient) => {
  const text = execFileSync("openssl", ["x509", "-in", cert, "-noout", "-text"], {
    encoding: "utf8",
  });
  assert.match(text, /CA:FALSE/u);
  assert.match(text, /TLS Web Server Authentication/u);
  assert.ok(text.includes(`DNS:${hostname}`));
  assert.equal(text.includes("http://revocation:8000/ca.crl"), recipient === "postgres");
  const verified = spawnSync("openssl", [
    "verify",
    "-CAfile",
    ca,
    "-purpose",
    "sslserver",
    "-verify_hostname",
    hostname,
    cert,
  ]);
  assert.equal(verified.status, 0);
  const wrongHost = spawnSync("openssl", [
    "verify",
    "-CAfile",
    ca,
    "-verify_hostname",
    "wrong.localhost",
    cert,
  ]);
  assert.notEqual(wrongHost.status, 0);
};

test("local browser leaves omit Docker revocation while database leaves retain it", () => {
  assert.ok(process.getuid && process.getgid, "Certificate creation requires POSIX ownership.");
  const uid = process.getuid();
  const gid = process.getgid();
  const root = mkdtempSync(join(tmpdir(), "claimcore-certificate-profiles-"));
  const authority = join(root, "authority");
  mkdirSync(authority);
  try {
    createAuthority(authority);
    for (const [hostname, recipient] of /** @type {const} */ ([
      ["app.localhost", "browser"],
      ["identity.localhost", "browser"],
      ["primary", "postgres"],
      ["witness", "postgres"],
    ])) {
      const directory = join(root, hostname);
      mkdirSync(directory);
      serverCertificate(authority, directory, { hostname, recipient }, uid, gid);
      const cert = join(directory, "server.pem");
      assertLeaf(cert, join(authority, "ca.pem"), hostname, recipient);
    }
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
