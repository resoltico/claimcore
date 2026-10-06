import { execFileSync } from "node:child_process";
import { chownSync } from "node:fs";
import { join } from "node:path";
import { privateFile } from "./local-files.mjs";

/** @param {string[]} args */
const openssl = (args) => execFileSync("openssl", args, { stdio: "ignore" });

/** @param {string} authority */
export function createAuthority(authority) {
  openssl([
    "req",
    "-x509",
    "-newkey",
    "rsa:3072",
    "-sha256",
    "-nodes",
    "-days",
    "365",
    "-subj",
    "/CN=ClaimCore Local Evaluation CA",
    "-addext",
    "basicConstraints=critical,CA:TRUE",
    "-addext",
    "keyUsage=critical,keyCertSign,cRLSign",
    "-keyout",
    join(authority, "ca.key"),
    "-out",
    join(authority, "ca.pem"),
  ]);
}

/** @param {string} hostname @param {string} key @param {string} csr */
const certificateRequest = (hostname, key, csr) => {
  openssl([
    "req",
    "-newkey",
    "rsa:3072",
    "-sha256",
    "-nodes",
    "-subj",
    `/CN=${hostname}`,
    "-keyout",
    key,
    "-out",
    csr,
  ]);
};

/** @param {string} authority @param {string} directory @param {{hostname: string, recipient: "browser" | "postgres"}} subject @param {number} uid @param {number} gid */
export function serverCertificate(authority, directory, subject, uid, gid) {
  const { hostname, recipient } = subject;
  if (recipient !== "browser" && recipient !== "postgres") {
    throw new Error("Unsupported local certificate recipient.");
  }
  const revocation =
    recipient === "postgres" ? "crlDistributionPoints=URI:http://revocation:8000/ca.crl\n" : "";
  const key = join(directory, "server.key");
  const csr = join(directory, "server.csr");
  const cert = join(directory, "server.pem");
  const ext = join(directory, "server.ext");
  privateFile(
    ext,
    `basicConstraints=critical,CA:FALSE\nkeyUsage=critical,digitalSignature,keyEncipherment\nextendedKeyUsage=serverAuth\nsubjectAltName=DNS:${hostname}\n${revocation}`,
    uid,
    gid,
  );
  certificateRequest(hostname, key, csr);
  openssl([
    "x509",
    "-req",
    "-in",
    csr,
    "-CA",
    join(authority, "ca.pem"),
    "-CAkey",
    join(authority, "ca.key"),
    "-CAcreateserial",
    "-days",
    "365",
    "-sha256",
    "-extfile",
    ext,
    "-out",
    cert,
  ]);
  for (const file of [key, csr, cert]) {
    chownSync(file, uid, gid);
  }
}

/** @param {string} directory */
export function webCertificate(directory) {
  openssl([
    "pkcs12",
    "-export",
    "-out",
    join(directory, "web.pfx"),
    "-inkey",
    join(directory, "server.key"),
    "-in",
    join(directory, "server.pem"),
    "-passout",
    "pass:",
  ]);
}
