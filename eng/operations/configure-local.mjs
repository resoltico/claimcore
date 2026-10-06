import { chownSync, lstatSync } from "node:fs";
import { join, resolve } from "node:path";
import { privateFile, jsonFile, emptyRoot } from "./local-files.mjs";
import { prepareLayout } from "./local-layout.mjs";
import { identityConfiguration } from "./local-identity.mjs";
import { runtimeKeys, webEnvironment } from "./local-runtime.mjs";
import { connectionFiles } from "./local-connections.mjs";

/** @param {string | undefined} value */
function id(value) {
  if (value === undefined || !/^[1-9][0-9]*$/u.test(value)) {
    throw new Error("Specify a non-root runtime UID and GID.");
  }
  const result = Number(value);
  if (!Number.isSafeInteger(result) || result > 2147483647) {
    throw new Error("Runtime UID/GID is outside the supported range.");
  }
  return result;
}

/** @param {string} root @param {number} uid @param {number} gid @param {string} zone */
function create(root, uid, gid, zone) {
  const owner = lstatSync(root).uid;
  emptyRoot(root, owner === 0 ? 0 : uid);
  chownSync(root, uid, gid);
  const identity = identityConfiguration();
  prepareLayout(root, uid, gid);
  runtimeKeys(join(root, "web"), uid, gid);
  connectionFiles(root, uid, gid);
  jsonFile(join(root, "identity"), "claimcore-realm.json", identity.realm, 1000, 0);
  privateFile(join(root, "identity", "admin.password"), `${identity.adminPassword}\n`, 1000, 0);
  privateFile(
    join(root, "administration", "owner.password"),
    `${identity.ownerPassword}\n`,
    uid,
    gid,
  );
  jsonFile(
    join(root, "administration"),
    "initial-owner.json",
    { issuer: identity.issuer, subject: identity.ownerSubject },
    uid,
    gid,
  );
  privateFile(join(root, "web", "oidc-client.secret"), `${identity.webSecret}\n`, uid, gid);
  const environment = Object.entries(webEnvironment(identity.issuer))
    .map(([key, value]) => `${key}=${value}`)
    .join("\n");
  privateFile(join(root, "web", "web.env"), `${environment}\n`, uid, gid);
  privateFile(join(root, "administration", "business-zone"), `${zone}\n`, uid, gid);
  jsonFile(
    root,
    "installation.json",
    {
      format: "claimcore-local-configuration-1",
      uid,
      gid,
      zone,
      origin: "https://app.localhost:5443",
      issuer: identity.issuer,
      scope: "SYNTHETIC_ONLY",
    },
    uid,
    gid,
  );
}

try {
  process.umask(0o077);
  const [directory, uid, gid, zone] = process.argv.slice(2);
  if (directory === undefined || zone === undefined || process.argv.length !== 6) {
    throw new Error("Usage: configure-local DIRECTORY UID GID BUSINESS_TIME_ZONE");
  }
  new Intl.DateTimeFormat("en", { timeZone: zone }).format(new Date());
  create(resolve(directory), id(uid), id(gid), zone);
  process.stdout.write(
    "Created persistent synthetic configuration. Credentials remain in private files.\n",
  );
} catch {
  process.stderr.write(
    "Local configuration creation failed. Existing or partial state is preserved; inspect inputs before another attempt.\n",
  );
  process.exitCode = 1;
}
