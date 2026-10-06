import { chownSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { privateDirectory, privateFile } from "./local-files.mjs";
import { createRevocation } from "./local-revocation.mjs";
import { createAuthority, serverCertificate, webCertificate } from "./local-certificates.mjs";

/** @param {string} root @param {number} uid @param {number} gid */
export function prepareLayout(root, uid, gid) {
  /** @type {[string, number, number][]} */
  const directories = [
    ["web", uid, gid],
    ["web-state", uid, gid],
    ["administration", uid, gid],
    ["authority", uid, gid],
    ["revocation", uid, gid],
    ["primary", 999, 999],
    ["witness", 999, 999],
    ["identity", 1000, 0],
  ];
  for (const [name, owner, group] of directories) {
    privateDirectory(join(root, name), owner, group);
  }
  createAuthority(join(root, "authority"));
  for (const name of ["ca.pem", "ca.key"]) {
    chownSync(join(root, "authority", name), uid, gid);
  }
  /** @type {[string, string, "browser" | "postgres", number, number][]} */
  const certificates = [
    ["web", "app.localhost", "browser", uid, gid],
    ["primary", "primary", "postgres", 999, 999],
    ["witness", "witness", "postgres", 999, 999],
    ["identity", "identity.localhost", "browser", 1000, 0],
  ];
  for (const [folder, hostname, recipient, owner, group] of certificates) {
    serverCertificate(
      join(root, "authority"),
      join(root, folder),
      { hostname, recipient },
      owner,
      group,
    );
  }
  webCertificate(join(root, "web"));
  chownSync(join(root, "web", "web.pfx"), uid, gid);
  createRevocation(join(root, "authority"), join(root, "revocation"), uid, gid);
  const ca = readFileSync(join(root, "authority", "ca.pem"));
  for (const folder of ["web", "administration"]) {
    privateFile(join(root, folder, "ca.pem"), ca, uid, gid);
  }
}
