import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { X509Certificate } from "node:crypto";
import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { homedir } from "node:os";
import { join } from "node:path";
import { isIP } from "node:net";

/** @returns {{ca: string, password: string, web: string, identity: string, expiry: string}} */
export function readFixture() {
  const source = readFileSync(0, "utf8");
  assert.ok(source.length < 65_536);
  /** @type {unknown} */
  const value = JSON.parse(source);
  assert.ok(value !== null && typeof value === "object");
  assert.deepEqual(Object.keys(value).sort(), ["ca", "expiry", "identity", "password", "web"]);
  assert.ok("ca" in value && typeof value.ca === "string");
  assert.ok("password" in value && typeof value.password === "string");
  assert.ok("web" in value && typeof value.web === "string" && isIP(value.web) === 4);
  assert.ok(
    "identity" in value && typeof value.identity === "string" && isIP(value.identity) === 4,
  );
  assert.ok("expiry" in value && typeof value.expiry === "string" && isIP(value.expiry) === 4);
  assert.ok(value.password.length > 0 && value.password.length <= 4096);
  const ca = new X509Certificate(value.ca);
  assert.ok(ca.ca);
  assert.equal(value.ca.trim(), ca.toString().trim());
  return {
    ca: value.ca,
    password: value.password,
    web: value.web,
    identity: value.identity,
    expiry: value.expiry,
  };
}

/** New container account only; a profile alone cannot isolate platform trust. @param {string} ca */
export function trustStore(ca) {
  assert.equal(process.getuid?.(), 1000);
  assert.equal(homedir(), "/home/node");
  const directory = join(homedir(), ".local/share/pki/nssdb");
  assert.equal(existsSync(directory), false);
  mkdirSync(directory, { recursive: true, mode: 0o700 });
  const certificate = join(directory, "claimcore-public-ca.pem");
  writeFileSync(certificate, ca, { mode: 0o600, flag: "wx" });
  const fingerprint = new X509Certificate(ca).fingerprint256;
  const nickname = `ClaimCore-${fingerprint.replaceAll(":", "")}`;
  /** @param {string[]} args */
  const certutil = (args) => execFileSync("/usr/bin/certutil", ["-d", `sql:${directory}`, ...args]);
  certutil(["-N", "--empty-password"]);
  const verify = () => {
    const imported = certutil(["-L", "-n", nickname, "-a"]);
    assert.equal(new X509Certificate(imported).fingerprint256, fingerprint);
  };
  return {
    import: () => {
      certutil(["-A", "-t", "C,,", "-n", nickname, "-i", certificate]);
      verify();
    },
    remove: () => {
      verify();
      certutil(["-D", "-n", nickname]);
    },
    fingerprint,
  };
}
